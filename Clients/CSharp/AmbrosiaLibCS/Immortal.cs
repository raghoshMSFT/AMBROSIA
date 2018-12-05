﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Xml;
using LocalAmbrosiaRuntime;
using Remote.Linq.Expressions;
using static Ambrosia.StreamCommunicator;

namespace Ambrosia
{
    [DataContract]
    [KnownType(typeof(ImmortalSerializerBase))]
    [KnownType(typeof(SerializableType))]
    [KnownType(typeof(SimpleImmortalSerializer))]
    [KnownType(typeof(SerializableCallCache))]
    [KnownType(typeof(SerializableTaskCompletionSource))]
    public abstract class Immortal : IDisposable
    {
        // Connection to the LocalAmbrosiaRuntime
        private NetworkStream _ambrosiaReceiveFromStream;
        private NetworkStream _ambrosiaSendToStream;
        private OutputConnectionRecord _ambrosiaSendToConnectionRecord;

        protected string localAmbrosiaRuntime; // if at least one method in this API requires a return address
        protected byte[] localAmbrosiaBytes;
        protected int localAmbrosiaBytesLength;

        private readonly SerializableBufferBlock<MessageContainer> _toTaskBuffer = new SerializableBufferBlock<MessageContainer>();
        private readonly SerializableBufferBlock<MessageContainer> _fromTaskBuffer = new SerializableBufferBlock<MessageContainer>();

        private bool _isFirstCheckpoint = true;
        private Dispatcher _dispatcher;
        private static FlexReadBuffer _inputFlexBuffer;
        private static int _cursor;

        [DataMember]
        [CopyFromDeserializedImmortal]
        public StringBuilder SerializedTask = new StringBuilder();

        /// <summary>
        /// Used when a client proxy sends a (non fire-and-forget) RPC. It is the unique identifier
        /// that is used when a reply comes back to get the corresponding continuation.
        /// </summary>
        [DataMember]
        [CopyFromDeserializedImmortal]
        public long CurrentSequenceNumber;

        /// <summary>
        /// This is the cache used to store the awaitable tasks for when a (non fire-and-forget) RPC call
        /// is made.
        /// </summary>
        [DataMember]
        [CopyFromDeserializedImmortal]
        public SerializableCallCache CallCache = new SerializableCallCache();

        public SerializableCache<int, long> TaskIdToSequenceNumber = new SerializableCache<int, long>();

        private ImmortalSerializerBase _immortalSerializer;

        /// <summary>
        /// Used to terminate the Dispatch task
        /// </summary>
        private readonly CancellationTokenSource cancelTokenSource = new CancellationTokenSource();

        /// <summary>
        /// Used to pass responsibility for the Dispatch loop to the most recently created
        /// task.
        /// </summary>
        protected ConcurrentQueue<int> dispatchTaskIdQueue = new ConcurrentQueue<int>();

        private readonly object myLock = new object();
        private readonly SameThreadTaskScheduler myTaskScheduler = new SameThreadTaskScheduler("AmbrosiaRPC");

        /// <summary>
        /// If this is deployed with upgrade information, then this is the interface type that should be deployed.
        /// </summary>
        private /*readonly*/ Type upgradeInterface;
        /// <summary>
        /// If this is deployed with upgrade information, then this is the type of which an instance is actually deployed.
        /// </summary>
        private /*readonly*/ Type upgradeImmortalType;

        protected abstract Task<bool> OnFirstStart();

        protected Task<Task> OnFirstStartWrapper()
        {
            return new Task<Task>(async () =>
            {
                try
                {
                    await this.OnFirstStart().RunWithCheckpointing(ref this.SerializedTask);
                }
                catch (Exception ex)
                {
                    this.HandleExceptionWrapper(ex);
                }
            });
        }

        protected void HandleExceptionWrapper(Exception ex)
        {
            try
            {
                this.HandleException(ex);
            }
            catch (Exception)
            {
                this.DefaultExceptionHandler(ex);
            }
        }

        protected virtual void HandleException(Exception ex)
        {
            this.DefaultExceptionHandler(ex);
        }

        protected void DefaultExceptionHandler(Exception ex)
        {
            Console.WriteLine($"{ex.Message}\n{ex.StackTrace}");
            Environment.Exit(1);
        }

        protected virtual void BecomingPrimary() { }

        // Hack for enabling fast IP6 loopback in Windows on .NET
        const int SIO_LOOPBACK_FAST_PATH = (-1744830448);

        private void SetupConnections(int receivePort, int sendPort, out NetworkStream receiveStream, out NetworkStream sendStream, out OutputConnectionRecord connectionRecord)
        {
            Socket mySocket = null;
            Byte[] optionBytes = BitConverter.GetBytes(1);

#if _WINDOWS
            mySocket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            mySocket.IOControl(SIO_LOOPBACK_FAST_PATH, optionBytes, null);
#else
            mySocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
#endif
            while (true)
            {
                try
                {
#if _WINDOWS
                    mySocket.Connect(IPAddress.IPv6Loopback, sendPort);
#else
                    mySocket.Connect(IPAddress.Loopback, sendPort);
#endif
                    break;
                }
                catch { }
            }
            TcpClient tcpSendToClient = new TcpClient();
            tcpSendToClient.Client = mySocket;
            sendStream = tcpSendToClient.GetStream();
            connectionRecord = new OutputConnectionRecord();
            connectionRecord.ConnectionStream = sendStream;
            connectionRecord.placeInOutput = new EventBuffer.BuffersCursor(null, -1, 0);
#if _WINDOWS
            var ipAddress = IPAddress.IPv6Loopback;
            mySocket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp);
            mySocket.IOControl(SIO_LOOPBACK_FAST_PATH, optionBytes, null);
#else
            var ipAddress = IPAddress.Loopback;
            mySocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
#endif
            var myReceiveFromEP = new IPEndPoint(ipAddress, receivePort);
            mySocket.Bind(myReceiveFromEP);
            mySocket.Listen(1);
            var socket = mySocket.Accept();
            receiveStream = new NetworkStream(socket);

            var processOutputTask = processOutputRequests();
        }

        private async Task processOutputRequests()
        {
            while (true)
            {
                var nextEntry = await _ambrosiaSendToConnectionRecord.WorkQ.DequeueAsync();
                AcquireOutputLock(1);
                if (nextEntry == -1)
                {
                    // This is a send output
                    Interlocked.Decrement(ref _ambrosiaSendToConnectionRecord._sendsEnqueued);
                    _ambrosiaSendToConnectionRecord.placeInOutput =
                            await _ambrosiaSendToConnectionRecord.BufferedOutput.SendAsync(_ambrosiaSendToStream, _ambrosiaSendToConnectionRecord.placeInOutput);
                }
                ReleaseOutputLock();
            }
        }

        protected T GetProxy<T>(string serviceName, bool attachNeeded = true)
        {
            if (this._dispatcher == null)
            {
                throw new InvalidOperationException("Need to deploy the container first!");
            }
            if (typeof(T).Equals(typeof(ConsoleImmortal)))
            {
                return (T)((object)new ConsoleImmortal(this, serviceName));
            }

            // Generate client container proxy and cache it.
            var typeOfT = typeof(T);
            var proxyClass = typeOfT.Assembly.GetType(typeOfT.FullName + "_Implementation");
            InstanceProxy.Immortal = this;
            var instance = Activator.CreateInstance(proxyClass, serviceName, attachNeeded);

            return (T)instance;

        }

        protected Task<Task> DispatchWrapper(int bytesToRead = 0)
        {
            return new Task<Task>(async () =>
            {
                try
                {
                    await this.Dispatch(bytesToRead);
                }
                catch (Exception ex)
                {
                    this.HandleException(ex);
                }
            });
        }

#pragma warning disable CS1998 // Async method lacks 'await' operators and will run synchronously
        protected async Task Dispatch(int bytesToRead = 0)
#pragma warning restore CS1998 // Async method lacks 'await' operators and will run synchronously
        {
#if DEBUG
            //Console.WriteLine($"Dispatch loop starting on task '{Thread.CurrentThread.ManagedThreadId}'");
#endif

            #region RPC Encoding
            //       |m|f|b| lFR|n| args|
            // |R|ret|
            //       |n|returnValue|
            // where:
            // R = RPC byte (byte)
            // ret = whether this is a return value or not (byte)

            // if it is *not* a return value
            // m = method id of the method that is being called (variable)
            // f = whether this is a fire-and-forget message (byte)
            // b = size of the return address (variable)
            // lFR = (bytes encoding) the return address
            // n = sequence number (variable)
            // args = serialized arguments, number and size baked into the generated code
            //
            // b, lFR, and n are optional (as a unit).
            // the value for f indicates whether they are present or not.

            // if it is a return value
            // n = sequence number (variable)
            // returnValue = serialized return value, size baked into the generated code

            #endregion

            _inputFlexBuffer = new FlexReadBuffer();
            int RPCsReceived = 0;

            while (!cancelTokenSource.IsCancellationRequested)
            {
                //Console.WriteLine("Waiting for next batch of messages from the LAR");

                lock (myLock)
                {
                    if (this.dispatchTaskIdQueue.Count > 1)
                    {
                        int x;
                        while (!this.dispatchTaskIdQueue.TryDequeue(out x)) { }
                        break; // some other dispatch loop will take over, so just die.
                    }
                }

                if (bytesToRead <= 24)
                {
                    int commitID = this._ambrosiaReceiveFromStream.ReadIntFixed();
                    bytesToRead = this._ambrosiaReceiveFromStream.ReadIntFixed();
                    long checkBytes = this._ambrosiaReceiveFromStream.ReadLongFixed();
                    long writeSeqID = this._ambrosiaReceiveFromStream.ReadLongFixed();
                }

                while (bytesToRead > 24)
                {
                    //Console.WriteLine("Waiting for the deserialization of a message from the LAR");

                    await FlexReadBuffer.DeserializeAsync(this._ambrosiaReceiveFromStream, _inputFlexBuffer);

                    bytesToRead -= _inputFlexBuffer.Length;

                    _cursor = _inputFlexBuffer.LengthLength; // this way we don't need to compute how much space was used to represent the length of the buffer.

                    var firstByte = _inputFlexBuffer.Buffer[_cursor];

                    switch (firstByte)
                    {
                        case AmbrosiaRuntime.InitalMessageByte:
                            {
#if DEBUG
                                Console.WriteLine("Received an initial message");
#endif

                                _cursor++;
                                var messageLength = _inputFlexBuffer.Buffer.ReadBufferedInt(_cursor);
                                _cursor += IntSize(messageLength);
                                var messageBuffer = new byte[messageLength];
                                Buffer.BlockCopy(_inputFlexBuffer.Buffer, _cursor, messageBuffer, 0, messageLength);
                                var message = Encoding.UTF8.GetString(messageBuffer);
                                // Actually, the message is just for fun. It is a signal to call OnFirstStart()
                                //Task.Factory.StartNew(
                                //    () => this.OnFirstStart()
                                //        , CancellationToken.None, TaskCreationOptions.DenyChildAttach
                                //        , myTaskScheduler
                                //    );
                                this.OnFirstStartWrapper().Start();

                                break;
                            }
                        case AmbrosiaRuntime.checkpointByte:
                            {
#if DEBUG
                                Console.WriteLine("Received a checkpoint message");
#endif
                                // TODO: this message should contain a (serialized - doh!) checkpoint. Restore the state.
                                _cursor++;

                                var sizeOfCheckpoint = _inputFlexBuffer.Buffer.ReadBufferedLong(_cursor);
                                _cursor += LongSize(sizeOfCheckpoint);
                                using (var readStreamWrapper = new PassThruReadStream(_ambrosiaReceiveFromStream, sizeOfCheckpoint))
                                {
                                    CopyFromDeserializedImmortal(readStreamWrapper);
                                }
                                break;
                            }

                        case AmbrosiaRuntime.takeCheckpointByte:
                            {
#if DEBUG
                                Console.WriteLine("Received a take checkpoint message");
#endif
                                _cursor++;

                                await this.TakeCheckpointAsync();

                                break;
                            }

                        case AmbrosiaRuntime.takeBecomingPrimaryCheckpointByte:
                            {
#if DEBUG
                                Console.WriteLine("Received a take checkpoint message");
#endif
                                _cursor++;

                                await this.TakeCheckpointAsync();
                                this.BecomingPrimary();

                                break;
                            }

                        case AmbrosiaRuntime.upgradeTakeCheckpointByte:
                        case AmbrosiaRuntime.upgradeServiceByte:
                            {
                                if (firstByte == AmbrosiaRuntime.upgradeTakeCheckpointByte)
                                {
#if DEBUG
                                    Console.WriteLine("Received a upgrade and take checkpoint message");
#endif
                                }
                                else
                                {
#if DEBUG
                                    Console.WriteLine("Received a upgrade service message");
#endif
                                }
                                _cursor++;

                                if (this.upgradeInterface == null || this.upgradeImmortalType == null)
                                {
                                    throw new Exception("Non-upgradeable deployment received an upgrade message.");
                                }

                                var newImmortal = (Immortal)Activator.CreateInstance(this.upgradeImmortalType, this);
                                var upgradedImmortalSerializerType = upgradeInterface.Assembly.GetType("Ambrosia.ImmortalSerializer");

                                var immortalSerializer = (ImmortalSerializerBase)Activator.CreateInstance(upgradedImmortalSerializerType);

                                var upgradedDispatcherType = upgradeInterface.Assembly.GetType(upgradeInterface.FullName + "_Dispatcher_Implementation");

                                var untypedProxy = Activator.CreateInstance(upgradedDispatcherType, newImmortal, immortalSerializer, "", -1 /*ignored when not setting up connections*/, -1 /*ignored when not setting up connections*/, false);
                                var typedProxy = (Dispatcher)untypedProxy;

                                // Copy over all of the state from this Immortal to the new one
                                foreach (var f in typeof(Immortal).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                                {
                                    f.SetValue(newImmortal, f.GetValue(this));
                                }

                                // IMPORTANT: But the value for the back pointer to the server proxy should be the newly generated proxy
                                newImmortal._dispatcher = typedProxy;

                                if (firstByte == AmbrosiaRuntime.upgradeTakeCheckpointByte)
                                {
                                    await newImmortal.TakeCheckpointAsync();
                                    newImmortal.BecomingPrimary();
                                }

                                // Start the new Immortal: start its Dispatch loop (BUT, perhaps still reading from the same page)
                                var t = newImmortal.DispatchWrapper(bytesToRead);

                                // IMPORTANT: set the pointer from the old server proxy to the new one
                                // This allows the new proxy to get disposed when (and not until!) the old
                                // one is disposed.
                                this._dispatcher.upgradedProxy = typedProxy;

                                // Need to die now, so do that by exiting loop
                                t.Start();
                                return;

                            }

                        case AmbrosiaRuntime.RPCByte:
                        case AmbrosiaRuntime.RPCBatchByte:
                        case AmbrosiaRuntime.CountReplayableRPCBatchByte:
                            {
                                RPCsReceived++;
                                var numberOfRPCs = 1;
                                var lengthOfCurrentRPC = 0;
                                int endIndexOfCurrentRPC = 0;

                                if (firstByte == AmbrosiaRuntime.RPCBatchByte || firstByte == AmbrosiaRuntime.CountReplayableRPCBatchByte)
                                {
                                    _cursor++;
                                    numberOfRPCs = _inputFlexBuffer.Buffer.ReadBufferedInt(_cursor);
                                    _cursor += IntSize(numberOfRPCs);
                                    if (firstByte == AmbrosiaRuntime.CountReplayableRPCBatchByte)
                                    {
                                        var numReplayableRPCs = _inputFlexBuffer.Buffer.ReadBufferedInt(_cursor);
                                        _cursor += IntSize(numReplayableRPCs);
                                    }
                                    //Console.WriteLine($"ServerImmortal received batch RPC (#{RPCsReceived}) with {numberOfRPCs} RPCs");
                                }
                                else
                                {
                                    //endIndexOfCurrentRPC = _inputFlexBuffer.Buffer.Length;
                                    endIndexOfCurrentRPC = _inputFlexBuffer.Length;
                                    //Console.WriteLine($"ServerImmortal received single RPC (#{RPCsReceived}). End index: {endIndexOfCurrentRPC}");
                                }

                                for (int i = 0; i < numberOfRPCs; i++)
                                {
                                    if (1 < numberOfRPCs)
                                    {
                                        lengthOfCurrentRPC = _inputFlexBuffer.Buffer.ReadBufferedInt(_cursor);
                                        _cursor += IntSize(lengthOfCurrentRPC);
                                        endIndexOfCurrentRPC = _cursor + lengthOfCurrentRPC;
                                    }

                                    var shouldBeRPCByte = _inputFlexBuffer.Buffer[_cursor];
                                    if (shouldBeRPCByte != AmbrosiaRuntime.RPCByte)
                                    {
                                        Console.WriteLine("UNKNOWN BYTE: {0}!!", shouldBeRPCByte);
                                        throw new Exception("Illegal leading byte in message");
                                    }
                                    _cursor++;

                                    var returnValueType = (ReturnValueTypes)_inputFlexBuffer.Buffer[_cursor++];

                                    if (returnValueType != ReturnValueTypes.None) // receiving a return value
                                    {
                                        var senderOfRPCLength = _inputFlexBuffer.Buffer.ReadBufferedInt(_cursor);
                                        var sizeOfSender = IntSize(senderOfRPCLength);
                                        _cursor += sizeOfSender;
                                        var senderOfRPC = Encoding.UTF8.GetString(_inputFlexBuffer.Buffer, _cursor, senderOfRPCLength);
                                        _cursor += senderOfRPCLength;

                                        var sequenceNumber = _inputFlexBuffer.Buffer.ReadBufferedLong(_cursor);
                                        _cursor += LongSize(sequenceNumber);

                                        //Console.WriteLine("Received RPC call to method with id: {0} and seq no.: {1}", methodId, CurrentSequenceNumber);
#if DEBUG
                                        Console.WriteLine($"Got response for {sequenceNumber} from {senderOfRPC}");
#endif

                                        if (this.CallCache.Data.TryRemove(sequenceNumber, out var taskCompletionSource))
                                        {
                                            switch (returnValueType)
                                            {
                                                case ReturnValueTypes.ReturnValue:
                                                    var deserializeNextValue = typeof(Immortal).GetMethods().FirstOrDefault(m => m.GetCustomAttributes(typeof(DeserializeNextValueAttribute)).Any());
                                                    if (deserializeNextValue != null)
                                                    {
                                                        var genericDeserializeNextValueMethod = deserializeNextValue.MakeGenericMethod(taskCompletionSource.ResultType.Type);
                                                        var result = genericDeserializeNextValueMethod.Invoke(this, null);

                                                        taskCompletionSource.SetResult(result);
                                                    }
                                                    break;
                                                case ReturnValueTypes.EmptyReturnValue:
                                                    taskCompletionSource.SetResult(GetDefault(taskCompletionSource.ResultType.Type));
                                                    break;
                                                case ReturnValueTypes.Exception:
                                                    var exceptionObj = DeserializeNextValue<object>();
                                                    var exception = new SerializableException(exceptionObj, senderOfRPC);
                                                    taskCompletionSource.SetException(exception);
                                                    break;
                                                default:
                                                    throw new ArgumentException($"Got an unfamiliar ReturnValueType: {returnValueType}");
                                            }
                                        }
                                        else
                                        {
                                            var errorMessage = $"Can't find sequence number {sequenceNumber} in cache";
                                            throw new InvalidOperationException(errorMessage);
                                        }
                                    }
                                    else // receiving an RPC
                                    {
                                        var methodId = _inputFlexBuffer.Buffer.ReadBufferedInt(_cursor);
                                        _cursor += IntSize(methodId);
                                        var rpcType = (RpcTypes.RpcType)_inputFlexBuffer.Buffer[_cursor++];

                                        string senderOfRPC = null;
                                        long sequenceNumber = 0;

                                        if (!rpcType.IsFireAndForget())
                                        {
                                            // read return address and sequence number
                                            var senderOfRPCLength = _inputFlexBuffer.Buffer.ReadBufferedInt(_cursor);
                                            var sizeOfSender = IntSize(senderOfRPCLength);
                                            _cursor += sizeOfSender;
                                            senderOfRPC = Encoding.UTF8.GetString(_inputFlexBuffer.Buffer, _cursor, senderOfRPCLength);
                                            _cursor += senderOfRPCLength;
                                            sequenceNumber = _inputFlexBuffer.Buffer.ReadBufferedLong(_cursor);
                                            _cursor += LongSize(sequenceNumber);
                                            //Console.WriteLine("Received RPC call to method with id: {0} and sequence number {1}", methodId, CurrentSequenceNumber);
                                        }
                                        else
                                        {

                                            //Console.WriteLine("Received fire-and-forget RPC call to method with id: {0}", methodId);
                                        }

                                        var lengthOfSerializedArguments = endIndexOfCurrentRPC - _cursor;
                                        byte[] localBuffer = new byte[lengthOfSerializedArguments];
                                        Buffer.BlockCopy(_inputFlexBuffer.Buffer, _cursor, localBuffer, 0, lengthOfSerializedArguments);

                                        //// BUGBUG: This works only if we are single-threaded and doing only fire-and-forget messages!
                                        //while (myTaskScheduler.NumberOfScheduledTasks() == myTaskScheduler.MaximumConcurrencyLevel)
                                        //{
                                        //    // just busy wait until there is a free thread in the scheduler
                                        //    // to handle this task.
                                        //}

                                        //Task.Factory.StartNew(
                                        //    () => _dispatcher.DispatchToMethod(methodId, fireAndForget, senderOfRPC, CurrentSequenceNumber, localBuffer, 0)
                                        //        , CancellationToken.None, TaskCreationOptions.DenyChildAttach
                                        //        , myTaskScheduler
                                        //    );
                                        try
                                        {
                                            await _dispatcher.DispatchToMethod(methodId, rpcType, senderOfRPC, sequenceNumber, localBuffer, 0);
                                        }
                                        catch (Exception ex)
                                        {
                                            Console.Write(ex.Message);
                                        }

                                        _cursor += lengthOfSerializedArguments;
                                    }

                                }
                                break;
                            }

                        default:
                            {
                                var s = String.Format("Illegal leading byte in message: {0}", firstByte);
#if DEBUG
                                Console.WriteLine(s);
#endif
                                throw new Exception(s);
                            }
                    }

                    _inputFlexBuffer.ResetBuffer();
                }
            }
        }

        public object GetDefault(Type t)
        {
            return this.GetType().GetMethod("GetDefaultGeneric").MakeGenericMethod(t).Invoke(this, null);
        }

        public T GetDefaultGeneric<T>()
        {
            return default(T);
        }

        public class MessageContainer
        {
            public long SequenceNumber { get; set; }
        }

        public class TakeCheckpointMessageContainer : MessageContainer
        {
            public bool IsTakeCheckpoint { get; set; }
        }

        public class SaveContextMessageContainer : MessageContainer
        {
        }

        public class CheckpointTakenMessageContainer : MessageContainer
        {
        }

        public class ContextSavedMessageContainer : MessageContainer
        {
        }

        public async Task<bool> ReplaceTaskInCacheAsync(Type resultType, long sequenceNumber)
        {
            var newTask = new SerializableTaskCompletionSource(resultType, sequenceNumber);
            await this.CallCache.Data.AddOrUpdate(sequenceNumber, newTask, (s, t) => newTask);

            return true;
        }

        public async Task<bool> GetResponseFromBufferAsync<T>(long sequenceNumber) where T : MessageContainer
        {
            while (true)
            {
                await this._fromTaskBuffer.Buffer.OutputAvailableAsync();

                var message = this._fromTaskBuffer.Buffer.Receive();
                if (message is T contextSavedMessageContainer &&
                    contextSavedMessageContainer.SequenceNumber == sequenceNumber)
                {
                    break;
                }
            }

            return true;
        }

        public async Task<bool> SaveTaskAsync()
        {
            var sequenceNumbers = this.CallCache.Data.Keys.ToList();
            var seqToSave = -1L;
            // Signal each outstanding async call to go through each call stack
            // This is done to assign a task ID to each call
            foreach (var sequenceNumber in sequenceNumbers)
            {
                if (!this.CallCache.Data.TryGetValue(sequenceNumber, out var stcs))
                {
                    throw new ArgumentException($"Unable to find sequence #{sequenceNumber} in the cache");
                }

                var saveContextMessageContainer = new SaveContextMessageContainer {SequenceNumber = sequenceNumber};
                this._toTaskBuffer.Buffer.Post(saveContextMessageContainer);

                await this.ReplaceTaskInCacheAsync(stcs.ResultType.Type, sequenceNumber);

                var setSaveContextMethod = typeof(SerializableTaskCompletionSource).GetMethods().FirstOrDefault(m =>
                    m.GetCustomAttributes(typeof(SerializableTaskCompletionSource.SetSaveContextAttribute)).Any());
                if (setSaveContextMethod != null)
                {
                    var genericSetSaveContextMethod = setSaveContextMethod.MakeGenericMethod(stcs.ResultType.Type);
                    genericSetSaveContextMethod.Invoke(stcs, new object[] { });
                }

                await this.GetResponseFromBufferAsync<ContextSavedMessageContainer>(sequenceNumber);

                // Choose one awaited async call to save its stack in order to resume from
                if (TaskCheckpoint.IsCurrentlyAwaited && seqToSave == -1)
                {
                    seqToSave = sequenceNumber;
                }
            }

            // Signal each outstanding async call to take a checkpoint
            // Only the call with sequence number == seqToSave would actually serialize its stack
            foreach (var sequenceNumber in sequenceNumbers)
            {
                var stcs = this.CallCache.Data[sequenceNumber];

                var saveTakeCheckpointMessageContainer = new TakeCheckpointMessageContainer {SequenceNumber = sequenceNumber, IsTakeCheckpoint = sequenceNumber == seqToSave};
                this._toTaskBuffer.Buffer.Post(saveTakeCheckpointMessageContainer);

                await this.ReplaceTaskInCacheAsync(stcs.ResultType.Type, sequenceNumber);

                var setTakeCheckpointMethod = typeof(SerializableTaskCompletionSource).GetMethods().FirstOrDefault(m =>
                    m.GetCustomAttributes(typeof(SerializableTaskCompletionSource.SetTakeCheckpointAttribute)).Any());
                if (setTakeCheckpointMethod != null)
                {
                    var genericSetTakeCheckpointMethod = setTakeCheckpointMethod.MakeGenericMethod(stcs.ResultType.Type);
                    genericSetTakeCheckpointMethod.Invoke(stcs, new object[] { });
                }

                await this.GetResponseFromBufferAsync<CheckpointTakenMessageContainer>(sequenceNumber);
            }

            return true;
        }

        private async Task TakeCheckpointAsync()
        {
            // wait for quiesence
            AcquireOutputLock(2);
            _ambrosiaSendToConnectionRecord.BufferedOutput.LockOutputBuffer();

            // Save current task state unless just resumed from a serialized task
            if (!this._isFirstCheckpoint || string.IsNullOrEmpty(this.SerializedTask.ToString()))
            {
                await this.SaveTaskAsync();
            }
            this._isFirstCheckpoint = false;

            // Second, serialize state and send checkpoint
            // Need to directly write checkpoint to the stream so it comes *before*
            var checkpointSize = _immortalSerializer.SerializeSize(this);
            var sizeOfMessage = 1 + LongSize(checkpointSize);
            _ambrosiaSendToStream.WriteInt(sizeOfMessage);
            _ambrosiaSendToStream.WriteByte(AmbrosiaRuntime.checkpointByte);
            _ambrosiaSendToStream.WriteLong(checkpointSize);
            using (var passThruStream = new PassThruWriteStream(_ambrosiaSendToStream))
            {
                _immortalSerializer.Serialize(this, passThruStream);
            }
            _ambrosiaSendToStream.Flush();
#if DEBUG
            Console.WriteLine("Sent checkpoint back to LAR");
#endif
            _ambrosiaSendToConnectionRecord.BufferedOutput.UnlockOutputBuffer();
            ReleaseOutputLock();
        }

        public async Task<ResultAdditionalInfo> TryTakeCheckpointContinuationAsync(ResultAdditionalInfo currentResultAdditionalInfo)
        {
            var result = (byte[])currentResultAdditionalInfo.Result;
            if (currentResultAdditionalInfo.AdditionalInfoType == ResultAdditionalInfoTypes.TakeCheckpoint)
            {
                var sequenceNumber = await this.TakeTaskCheckpointAsync();
                this.StartDispatchLoop();
                var resultAdditionalInfo = await this.GetTaskToWaitForWithAdditionalInfoAsync(sequenceNumber); // Re-await original task
                result = (byte[])resultAdditionalInfo.Result;
            }

            return new ResultAdditionalInfo(result, currentResultAdditionalInfo.ResultType.Type);
        }

        public async Task<bool> TrySaveContextContinuationAsync(ResultAdditionalInfo currentResult)
        {
            if (currentResult.AdditionalInfoType == ResultAdditionalInfoTypes.SaveContext)
            {
                await this.SaveTaskContextAsync();
                return true;
            }

            return false;
        }

        public async Task<long> TakeTaskCheckpointAsync()
        {
            var sequenceNumber = -1L;
            var message = await this._toTaskBuffer.Buffer.ReceiveAsync();

            if (message is TakeCheckpointMessageContainer saveTaskMessage)
            {
                sequenceNumber = saveTaskMessage.SequenceNumber;
                if (saveTaskMessage.IsTakeCheckpoint)
                {
                    await Task.Delay(100);
                    await TaskCheckpoint.Save();
                }
                
                var messageContainer = new CheckpointTakenMessageContainer { SequenceNumber = sequenceNumber };
                this._fromTaskBuffer.Buffer.Post(messageContainer);
            }

            return sequenceNumber;
        }

        public async Task<long> SaveTaskContextAsync()
        {
            var sequenceNumber = -1l;
            var message = await this._toTaskBuffer.Buffer.ReceiveAsync();

            if (message is SaveContextMessageContainer saveContextMessage)
            {
                sequenceNumber = saveContextMessage.SequenceNumber;
                await Task.Delay(100);
                await TaskCheckpoint.SaveContext();
                var messageContainer = new ContextSavedMessageContainer { SequenceNumber = sequenceNumber };
                this._fromTaskBuffer.Buffer.Post(messageContainer);
            }

            return sequenceNumber;
        }

        public void StartDispatchLoop()
        {
            var t = this.DispatchWrapper();
            this.dispatchTaskIdQueue.Enqueue(t.Id);
            t.Start();
        }

        public Task<T> GetTaskToWaitForAsync<T>(long sequenceNumber)
        {
            if (!this.CallCache.Data.TryGetValue(sequenceNumber, out var stcs))
            {
                throw new ArgumentException($"Unable to get sequence number {sequenceNumber} from call cache.");
            }

            return stcs.GetAwaitableTaskAsync<T>();
        }

        public Task<ResultAdditionalInfo> GetTaskToWaitForWithAdditionalInfoAsync(long sequenceNumber)
        {
            if (!this.CallCache.Data.TryGetValue(sequenceNumber, out var stcs))
            {
                throw new ArgumentException($"Unable to get sequence number {sequenceNumber} from call cache.");
            }

            return stcs.GetAwaitableTaskWithAdditionalInfoAsync();
        }

        [AttributeUsage(AttributeTargets.Field)]
        public class CopyFromDeserializedImmortalAttribute : Attribute
        {

        }

        private void CopyFromDeserializedImmortal(Stream dataStream)
        {
            var otherImmortal = this._immortalSerializer.Deserialize(this.GetType(), dataStream);
            // Use the deserialized object as the source for copying fields/properties to this instance.
            // NB: Should this be the other way around? Then we would know which fields to copy over (those
            // from the base class) and then we could invoke Dispatch on the newly deserialized instance
            // and throw away the current instance.
            var typeOfObject = this.GetType(); // want the dynamic (sub) type
            foreach (var memberInfo in typeOfObject.GetMembers(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!(memberInfo.MemberType == MemberTypes.Field || memberInfo.MemberType == MemberTypes.Property) ||
                    (!memberInfo.DeclaringType.IsSubclassOf(typeof(Immortal)) && !memberInfo.GetCustomAttributes(typeof(CopyFromDeserializedImmortalAttribute)).Any()))
                {
                    continue;
                }
                if (memberInfo.MemberType == MemberTypes.Field || memberInfo.MemberType == MemberTypes.Property)
                {
                    var fi = (FieldInfo)memberInfo;
                    var v = fi.GetValue(otherImmortal);
                    fi.SetValue(this, v);
                }
                else
                {
                    throw new InvalidOperationException("Should never get here.");
                }
            }
            InstanceProxy.Immortal = this;
        }

        // Bundling at source
        private EventBuffer.BufferPage StartRPC<T>(
            int methodIdentifier,
            int lengthOfSerializedArguments,
            out SerializableTaskCompletionSource taskToWaitFor,
            RpcTypes.RpcType rpcType = RpcTypes.RpcType.ReturnValue,
            byte[] encodedDestinationLFR = null,
            int encodedDestinationLFRLength = 0 // note that this could be computed on each call: encodedDestinationLFR.Length
            )
        {
            //Console.WriteLine("ClientImmortal starting RPC call");

            #region Encoding
            // |s|R|a| rFR|ret|m|f|b| lFR|n| args|
            // where:
            // s = size of the entire message (variable)
            // R = RPC byte (byte)
            // a = length of the remote Ambrosia Runtime (variable)
            // rFR = bytes encoding the name of the remote Ambrosia Runtime
            // ret = whether this is a return value or not (byte)
            // m = method id of the method that is being called (variable)
            // f = whether this is a fire-and-forget message (byte)
            // b = size of the return address (variable)
            // lFR = (bytes encoding) the return address
            // n = sequence number (variable)
            // args = serialized arguments, number and size baked into the generated code
            //
            // b, lFR, and n are optional (as a unit).
            // the value for f indicates whether they are present or not.
            #endregion

            taskToWaitFor = null;
            long newSequenceNumber = 0;

            int optionalPartSize = 0;

            if (!rpcType.IsFireAndForget())
            {
                newSequenceNumber = Interlocked.Increment(ref this.CurrentSequenceNumber);

                optionalPartSize =
                    IntSize(this.localAmbrosiaBytesLength) // size of the length of the return address
                    + this.localAmbrosiaBytesLength // size of the return address
                    + LongSize(this.CurrentSequenceNumber) // size of the sequence number
                    ;
            }

            var bytesPerMessageWithoutSerializedArguments =
                1 // RPCByte
                + IntSize(encodedDestinationLFRLength) // size of the length of the destination bytes
                + encodedDestinationLFRLength // size of the destination bytes
                + 1 // size of the return-value flag byte
                + IntSize(methodIdentifier) // size of the method identifier
                + 1 // size of the fire-and-forget value
                + optionalPartSize
                ;

            var bytesPerMessage =
                bytesPerMessageWithoutSerializedArguments
                + lengthOfSerializedArguments
                ;

            var totalBytesPerMessage = bytesPerMessage + IntSize(bytesPerMessage);

            var writablePage = this._ambrosiaSendToConnectionRecord.BufferedOutput.getWritablePage(totalBytesPerMessage);
            writablePage.NumMessages++;
            var localBuffer = writablePage.PageBytes;

            writablePage.curLength += localBuffer.WriteInt(writablePage.curLength, bytesPerMessage);

            // Write byte signalling that this is a RPC call
            localBuffer[writablePage.curLength++] = AmbrosiaRuntime.RPCByte;

            // Write destination length, followed by the destination
            writablePage.curLength += localBuffer.WriteInt(writablePage.curLength, encodedDestinationLFRLength);
            Buffer.BlockCopy(encodedDestinationLFR, 0, localBuffer, writablePage.curLength, encodedDestinationLFRLength);
            writablePage.curLength += encodedDestinationLFRLength;

            // Write return-value flag byte
            localBuffer[writablePage.curLength++] = (byte) ReturnValueTypes.None; // this is *not* a return value, but the call

            // Write "name" of method
            writablePage.curLength += localBuffer.WriteInt(writablePage.curLength, methodIdentifier);
            // Write whether this is fire-and-forget
            localBuffer[writablePage.curLength++] = (byte)rpcType;

            // Write optional part depending on whether it is fire-and-forget
            if (!rpcType.IsFireAndForget())
            {
                writablePage.curLength += localBuffer.WriteInt(writablePage.curLength, this.localAmbrosiaBytesLength);
                Buffer.BlockCopy(this.localAmbrosiaBytes, 0, localBuffer, writablePage.curLength, this.localAmbrosiaBytesLength);
                writablePage.curLength += this.localAmbrosiaBytesLength;

                writablePage.curLength += localBuffer.WriteLong(writablePage.curLength, newSequenceNumber);

                taskToWaitFor = new SerializableTaskCompletionSource(typeof(T), newSequenceNumber);
                this.CallCache.Data.TryAdd(newSequenceNumber, taskToWaitFor);
#if DEBUG
                Console.WriteLine("Sent request for {0}", newSequenceNumber);
#endif
            }

            if (methodIdentifier == 0 || !rpcType.IsFireAndForget())
            {
                // Sending this RPC call might trigger an incoming call to this container.
                // But the dispatch loop for this container might be blocked, so start up a new one.
                var t = this.DispatchWrapper();
                this.dispatchTaskIdQueue.Enqueue(t.Id);
                //Console.WriteLine("Starting a new dispatch loop from StartRPC (task {0})", t.Id);
                //t.Start(myTaskScheduler);
                t.Start();
            }

            return writablePage;

        }

        [AttributeUsage(AttributeTargets.Method)]
        public class DeserializeNextValueAttribute : Attribute
        {

        }

        [DeserializeNextValue]
        public static T DeserializeNextValue<T>()
        {
            return (T)DeserializeNextValue(typeof(T));
        }

        public static object DeserializeNextValue(Type typeOfValue)
        {
            var dataContractAttribute = typeOfValue.GetCustomAttribute<DataContractAttribute>();
            var serializableAttribute = typeOfValue.GetCustomAttribute<SerializableAttribute>();

            if ((!typeOfValue.IsArray || typeOfValue.GetElementType() != typeof(byte)) &&
                dataContractAttribute == null &&
                serializableAttribute == null)
                throw new NotImplementedException("Need to handle the type: " + typeOfValue);

            var valLength = _inputFlexBuffer.Buffer.ReadBufferedInt(_cursor);
            _cursor += IntSize(valLength);
            var valBuffer = new byte[valLength];
            Buffer.BlockCopy(_inputFlexBuffer.Buffer, _cursor, valBuffer, 0, valLength);
            _cursor += valLength;

            if (typeOfValue.IsArray && typeOfValue.GetElementType() == typeof(byte))
            {
                return valBuffer;
            }

            return BinarySerializer.Deserialize(typeOfValue, valBuffer);
        }

        private void ReleaseBufferAndSend(bool doTheSend = true)
        {
            //Console.WriteLine("Immortal releasing buffer and sending RPC");

            _ambrosiaSendToConnectionRecord.BufferedOutput.UnlockOutputBuffer();

            if (doTheSend)
            {
                // Make sure there is a send enqueued in the work Q.
                if (_ambrosiaSendToConnectionRecord._sendsEnqueued == 0)
                {
                    Interlocked.Increment(ref _ambrosiaSendToConnectionRecord._sendsEnqueued);
                    _ambrosiaSendToConnectionRecord.WorkQ.Enqueue(-1);
                }
            }
        }

        public class SimpleImmortalSerializer : ImmortalSerializerBase
        {
            public override long SerializeSize(Immortal c)
            {
                var serializer = new DataContractSerializer(c.GetType(), this.KnownTypes.Select(kt => kt.Type).ToArray());
                long retVal = -1;
                using (var countStream = new CountStream())
                {
                    using (var writer = XmlDictionaryWriter.CreateBinaryWriter(countStream))
                    {
                        serializer.WriteObject(writer, c);
                    }
                    retVal = countStream.Length;
                }
                return retVal;
            }

            public override void Serialize(Immortal c, Stream writeToStream)
            {
                // nned to create
                var serializer = new DataContractSerializer(c.GetType(), this.KnownTypes.Select(kt => kt.Type).ToArray());
                using (var writer = XmlDictionaryWriter.CreateBinaryWriter(writeToStream))
                {
                    serializer.WriteObject(writer, c);
                }
            }

            public override Immortal Deserialize(Type runtimeType, Stream stream)
            {
                var serializer = new DataContractSerializer(runtimeType, this.KnownTypes.Select(kt => kt.Type).ToArray());
                using (var reader = XmlDictionaryReader.CreateBinaryReader(stream, XmlDictionaryReaderQuotas.Max))
                {
                    return (Immortal)serializer.ReadObject(reader);
                }
            }
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls
        volatile private int _quiesce;
        private long _outputLock;

        internal void AcquireOutputLock(long lockVal = 1)
        {
            while (true)
            {
                var origVal = Interlocked.CompareExchange(ref _outputLock, lockVal, 0);
                if (origVal == 0)
                {
                    // We have the lock
                    break;
                }
            }
        }

        internal void ReleaseOutputLock()
        {
            Interlocked.Exchange(ref _outputLock, 0);
        }


        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
#if DEBUG
                    Console.WriteLine("Dispatcher disposing");
#endif
                    // TODO: dispose managed state (managed objects).
                    this.cancelTokenSource.Cancel(true);
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~Dispatcher() {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }
        #endregion

        #region application-defined save/restore

        /// <summary>
        /// Called when performing snapshots.
        /// </summary>
        protected virtual void OnSave(Stream stream)
        {
        }
        /// <summary>
        /// Called when restoring from a snapshot.
        /// </summary>
        protected virtual void OnRestore(Stream stream)
        {
        }

        [DataMember]
        private byte[] _checkpointBuf;

        [OnSerializing()]
        internal void SerializeUserDefinedApplicationState(StreamingContext context)
        {
            var memStream = new MemoryStream();
            OnSave(memStream);
            _checkpointBuf = memStream.GetBuffer();
        }

        [OnSerialized()]
        internal void SerializeUserDefinedApplicationStateCleanup(StreamingContext context)
        {
            _checkpointBuf = null;
        }

        [OnDeserialized()]
        internal void DeserializeUserDefinedApplicationState(StreamingContext context)
        {
            var memStream = new MemoryStream(_checkpointBuf);
            OnRestore(memStream);
            _checkpointBuf = null;
        }

        #endregion

        [DataContract]
        public class InstanceProxy
        {
            public static Immortal Immortal { get; protected internal set; }

            [DataMember]
            protected readonly string remoteAmbrosiaRuntime;
            [DataMember]
            protected readonly byte[] remoteAmbrosiaBytes;
            [DataMember]
            protected readonly int remoteAmbrosiaBytesLength;

            public InstanceProxy(string remoteAmbrosiaRuntime, bool attachNeeded)
            {
#if DEBUG
                Console.WriteLine($"InstanceProxy created to communicate with {remoteAmbrosiaRuntime}. (Attach: {attachNeeded})");
#endif
                this.remoteAmbrosiaRuntime = remoteAmbrosiaRuntime;
                this.remoteAmbrosiaBytes = Encoding.UTF8.GetBytes(this.remoteAmbrosiaRuntime);
                this.remoteAmbrosiaBytesLength = this.remoteAmbrosiaBytes.Length;

                if (attachNeeded)
                {
                    Immortal.AcquireOutputLock(3);
#if DEBUG
                    Console.WriteLine("Sending attach message to: " + this.remoteAmbrosiaRuntime);
#endif
                    // Send attach message to the remote Ambrosia Runtime
                    var destinationBytes = Encoding.UTF8.GetBytes(this.remoteAmbrosiaRuntime);
                    // Write message size
                    Immortal._ambrosiaSendToStream.WriteInt(1 + destinationBytes.Length);
                    // Write message type
                    Immortal._ambrosiaSendToStream.WriteByte(AmbrosiaRuntime.attachToByte);
                    // Write Destination
                    Immortal._ambrosiaSendToStream.Write(destinationBytes, 0, destinationBytes.Length);
                    Immortal.ReleaseOutputLock();
                }
            }

            protected void ReleaseBufferAndSend() { Immortal.ReleaseBufferAndSend(); }

            protected EventBuffer.BufferPage StartRPC<T>(
                int methodIdentifier,
                int lengthOfSerializedArguments,
                out SerializableTaskCompletionSource taskToWaitFor,
                RpcTypes.RpcType rpcType = RpcTypes.RpcType.ReturnValue,
                byte[] encodedDestinationLFR = null,
                int encodedDestinationLFRLength = 0 // note that this could be computed on each call: encodedDestinationLFR.Length
                )
            {
                return Immortal.StartRPC<T>(methodIdentifier, lengthOfSerializedArguments, out taskToWaitFor, rpcType, this.remoteAmbrosiaBytes, this.remoteAmbrosiaBytesLength);
            }
        }

        public abstract class Dispatcher : IDisposable
        {
            protected readonly Immortal MyImmortal;
            protected readonly ImmortalSerializerBase MyImmortalSerializer;
            internal Dispatcher upgradedProxy;

            public Dispatcher(Immortal myImmortal, ImmortalSerializerBase myImmortalSerializer, string localAmbrosiaRuntime, int receivePort, int sendPort, bool setupConnections)
            {
                this.MyImmortal = myImmortal;
                this.MyImmortalSerializer = myImmortalSerializer;
                this.MyImmortal._immortalSerializer = this.MyImmortalSerializer;
                #region Since the creation of a Immortal is under user control, this is is initialization stuff that happens only upon deployment
                this.MyImmortal._dispatcher = this;
                this.MyImmortal.localAmbrosiaRuntime = localAmbrosiaRuntime;
                this.MyImmortal.localAmbrosiaBytes = Encoding.UTF8.GetBytes(localAmbrosiaRuntime);
                this.MyImmortal.localAmbrosiaBytesLength = this.MyImmortal.localAmbrosiaBytes.Length;
                if (setupConnections)
                {
                    this.MyImmortal.SetupConnections(receivePort, sendPort, out this.MyImmortal._ambrosiaReceiveFromStream, out this.MyImmortal._ambrosiaSendToStream, out this.MyImmortal._ambrosiaSendToConnectionRecord);
                }
                var baseType = myImmortal.GetType().BaseType;
                if (baseType.IsGenericType)
                {
                    // then the user created a subtype of Immortal<T> instead of Immortal
                    // need to set the self-proxy field
                    var getProxyMethodDef = typeof(Immortal).GetMethod("GetProxy", BindingFlags.NonPublic | BindingFlags.Instance);
                    var genericProxyMethod = getProxyMethodDef.MakeGenericMethod(baseType.GetGenericArguments().First());
                    var selfProxy = genericProxyMethod.Invoke(myImmortal, new object[] { "", false, });
                    var selfProxyField = baseType.GetField("thisProxy", BindingFlags.NonPublic | BindingFlags.Instance);
                    selfProxyField.SetValue(myImmortal, selfProxy);
                }
                #endregion

            }
            public Dispatcher(Immortal myImmortal, ImmortalSerializerBase myImmortalSerializer, string localAmbrosiaRuntime, Type newInterface, Type newImmortalType, int receivePort, int sendPort)
                : this(myImmortal, myImmortalSerializer, localAmbrosiaRuntime, receivePort, sendPort, true)
            {
                this.MyImmortal.upgradeInterface = newInterface;
                this.MyImmortal.upgradeImmortalType = newImmortalType;
            }

            public void Start()
            {
#if DEBUG
                Console.WriteLine("Start Start()");
#endif
                var inputFlexBuffer = new FlexReadBuffer();
                int commitID = MyImmortal._ambrosiaReceiveFromStream.ReadIntFixed();
                int bytesToRead = MyImmortal._ambrosiaReceiveFromStream.ReadIntFixed();
                long checkBytes = MyImmortal._ambrosiaReceiveFromStream.ReadLongFixed();
                long writeSeqID = MyImmortal._ambrosiaReceiveFromStream.ReadLongFixed();
                var _ = FlexReadBuffer.DeserializeAsync(MyImmortal._ambrosiaReceiveFromStream, inputFlexBuffer).Result;

                bytesToRead -= inputFlexBuffer.Length;

                var cursor = inputFlexBuffer.LengthLength; // this way we don't need to compute how much space was used to represent the length of the buffer.

                var firstByte = inputFlexBuffer.Buffer[cursor++];

                if (firstByte == AmbrosiaRuntime.checkpointByte)
                {
                    // Then this container is recovering
#if DEBUG
                    Console.WriteLine("Received a checkpoint message");
#endif
                    // TODO: this message should contain a (serialized - doh!) checkpoint. Restore the state.
                    var sizeOfCheckpoint = inputFlexBuffer.Buffer.ReadBufferedLong(cursor);
                    cursor += LongSize(sizeOfCheckpoint);
                    using (var readStreamWrapper = new PassThruReadStream(MyImmortal._ambrosiaReceiveFromStream, sizeOfCheckpoint))
                    {
                        this.MyImmortal.CopyFromDeserializedImmortal(readStreamWrapper);
                    }
                    MyImmortal._immortalSerializer = this.MyImmortalSerializer;
#if DEBUG
                    Console.WriteLine($"Deserialized: {this.MyImmortal.ToString()}");
#endif
                    if (!string.IsNullOrEmpty(this.MyImmortal.SerializedTask.ToString()))
                    {
                        var resumeMainTask = new Task(async () => await this.MyImmortal.Resume());
                        resumeMainTask.Start();
                    }
                    else
                    {
                        // Now that the state is restored, start listening for incoming messages
                        var t = Dispatch();
                        this.MyImmortal.dispatchTaskIdQueue.Enqueue(t.Id);
                        //Console.WriteLine("Starting a new dispatch loop from StartRPC (task {0})", t.Id);
                        //t.Start(myTaskScheduler);
                        t.Start();
                    }
                }
                else if (firstByte == AmbrosiaRuntime.takeCheckpointByte || firstByte == AmbrosiaRuntime.takeBecomingPrimaryCheckpointByte)
                {
                    // Then this container is starting for the first time
                    if (firstByte == AmbrosiaRuntime.takeCheckpointByte)
                    {
#if DEBUG
                        Console.WriteLine("Received a take checkpoint message");
#endif
                    }
                    else
                    {
#if DEBUG
                        Console.WriteLine("Received a take becoming primary checkpoint message");
#endif
                    }
                    int sizeOfMessage;

                    // First, send the "initial message"  so that it will be reflected in the saved state in LAR
                    // That way, if recovery happens after the first checkpoint (i.e., before a second checkpoint)
                    // this message will get sent back to this container so it will restart properly
                    {
                        MyImmortal.AcquireOutputLock(2);
                        var initialMessageBytes = Encoding.UTF8.GetBytes("hello");
                        var initialMessageSize = initialMessageBytes.Length;
                        sizeOfMessage = 1 + IntSize(initialMessageSize) + initialMessageSize;
                        MyImmortal._ambrosiaSendToStream.WriteInt(sizeOfMessage);
                        MyImmortal._ambrosiaSendToStream.WriteByte(AmbrosiaRuntime.InitalMessageByte);
                        MyImmortal._ambrosiaSendToStream.WriteInt(initialMessageSize);
                        MyImmortal._ambrosiaSendToStream.Write(initialMessageBytes, 0, initialMessageSize);
                        MyImmortal._ambrosiaSendToStream.Flush();
                        MyImmortal.ReleaseOutputLock();
                    }

#if DEBUG
                    Console.WriteLine("Sent initial message to LAR");
#endif

                    //// Side effect of calling StartRPC is to kick off the Dispatch loop in a different thread
                    //Task<object> rpcTask;
                    //var wp = Immortal.StartRPC(0 /* method identifier for OnFirstStart */, 0, null, out rpcTask, fireAndForget: true, encodedDestinationLFR: new byte[0], encodedDestinationLFRLength: 0);
                    //this.ReleaseBufferAndSend(doTheSend: false);

                    // Second, serialize state and send checkpoint
                    // Need to directly write checkpoint to the stream so it comes *before* the RPC call

                    {
                        // wait for quiesence
                        MyImmortal.AcquireOutputLock(2);
                        MyImmortal._ambrosiaSendToConnectionRecord.BufferedOutput.LockOutputBuffer();

                        var checkpointSize = this.MyImmortal._immortalSerializer.SerializeSize(this.MyImmortal);
                        sizeOfMessage = 1 + LongSize(checkpointSize);
                        MyImmortal._ambrosiaSendToStream.WriteInt(sizeOfMessage);
                        MyImmortal._ambrosiaSendToStream.WriteByte(AmbrosiaRuntime.checkpointByte);
                        MyImmortal._ambrosiaSendToStream.WriteLong(checkpointSize);
                        using (var passThruStream = new PassThruWriteStream(MyImmortal._ambrosiaSendToStream))
                        {
                            this.MyImmortal._immortalSerializer.Serialize(this.MyImmortal, passThruStream);
                        }
                        MyImmortal._ambrosiaSendToStream.Flush();

                        MyImmortal._ambrosiaSendToConnectionRecord.BufferedOutput.UnlockOutputBuffer();
                        MyImmortal.ReleaseOutputLock();
                    }


#if DEBUG
                    Console.WriteLine("Sent checkpoint back to LAR");
#endif

                    if (firstByte == AmbrosiaRuntime.takeBecomingPrimaryCheckpointByte)
                    {
                        this.MyImmortal.BecomingPrimary();
                    }

                    // Starting the Dispatch loop is now done as part of StartRPC.
                    // The first step above means that the Dispatch loop will take care of calling the OnFirstStart method.

                    // Third, start the dispatch loop as a new task
                    //Task.Factory.StartNew(() => Immortal.Dispatch());
                    var t = Dispatch();
                    this.MyImmortal.dispatchTaskIdQueue.Enqueue(t.Id);
                    t.Start();
                }
                else
                {
                    var s = String.Format("Start() received an illegal leading byte in first message: {0}", firstByte);
#if DEBUG
                    Console.WriteLine(s);
#endif
                    throw new Exception(s);
                }
#if DEBUG
                Console.WriteLine("End Start()");
#endif
            }

            public Task<Task> Dispatch()
            {
                return this.MyImmortal.DispatchWrapper();
            }

            public void EntryPoint() { this.MyImmortal.OnFirstStartWrapper(); }
            protected void ReleaseBufferAndSend(bool doTheSend = true) { this.MyImmortal.ReleaseBufferAndSend(doTheSend: doTheSend); }
            public abstract Task<bool> DispatchToMethod(int methodId, RpcTypes.RpcType rpcType, string senderOfRPC, long sequenceNumber, byte[] buffer, int cursor);

            // Bundling at source
            protected EventBuffer.BufferPage StartRPC_ReturnValue(string destination, long sequenceNumber, int lengthOfSerializedArguments, ReturnValueTypes returnValueType)
            {
                //Console.WriteLine("ServerImmortal starting RPC response");

                #region Encoding
                // |s|R|a| rFR|ret|b| lFR|n|returnValue|
                // where:
                // s = size of the entire message (variable)
                // R = RPC byte (byte)
                // a = length of the destination (variable)
                // rFR = bytes encoding the name of the destination
                // ret = whether this is a return value or not (byte)
                // b = size of the return address (variable)
                // lFR = (bytes encoding) the return address
                // n = sequence number (variable)
                // returnValue = serialized return value, size baked into the generated code
                #endregion

                var destinationBytes = Encoding.UTF8.GetBytes(destination);

                var bytesPerMessageWithoutSerializedReturnValue =
                    1 // RPCByte
                    + IntSize(destinationBytes.Length) // size of the length of the destination bytes
                    + destinationBytes.Length // size of the destination bytes
                    + 1 // size of the return-value flag byte
                    + IntSize(this.MyImmortal.localAmbrosiaBytesLength) // size of the length of the return address
                    + this.MyImmortal.localAmbrosiaBytesLength // size of the return address
                    + LongSize(sequenceNumber) // size of the sequence number
                    ;

                var bytesPerMessage =
                    bytesPerMessageWithoutSerializedReturnValue
                    + lengthOfSerializedArguments
                    ;

                var totalBytesPerMessage = bytesPerMessage + IntSize(bytesPerMessage);

                var writablePage = MyImmortal._ambrosiaSendToConnectionRecord.BufferedOutput.getWritablePage(totalBytesPerMessage);
                writablePage.NumMessages++;
                var localBuffer = writablePage.PageBytes;

                writablePage.curLength += localBuffer.WriteInt(writablePage.curLength, bytesPerMessage);

                // Write byte signalling that this is a RPC call
                localBuffer[writablePage.curLength++] = AmbrosiaRuntime.RPCByte;

                // Write destination length, followed by the destination
                writablePage.curLength += localBuffer.WriteInt(writablePage.curLength, destinationBytes.Length);
                Buffer.BlockCopy(destinationBytes, 0, localBuffer, writablePage.curLength, destinationBytes.Length);
                writablePage.curLength += destinationBytes.Length;

                // Write return-value flag byte
                localBuffer[writablePage.curLength++] = (byte) returnValueType; // this *is* a return value, not the call

                writablePage.curLength += localBuffer.WriteInt(writablePage.curLength, this.MyImmortal.localAmbrosiaBytesLength);
                Buffer.BlockCopy(this.MyImmortal.localAmbrosiaBytes, 0, localBuffer, writablePage.curLength, this.MyImmortal.localAmbrosiaBytesLength);
                writablePage.curLength += this.MyImmortal.localAmbrosiaBytesLength;

                writablePage.curLength += localBuffer.WriteLong(writablePage.curLength, sequenceNumber);

                return writablePage;
            }

            public virtual void Dispose()
            {
                MyImmortal.Dispose();
                if (upgradedProxy != null)
                {
                    upgradedProxy.Dispose();
                }
            }
        }

        public async Task Resume()
        {
            if (this.SerializedTask != null)
            {
                var resumedTask = TaskCheckpoint.ResumeFrom(this, ref this.SerializedTask, out var locals);
                await resumedTask.RunWithCheckpointing(ref this.SerializedTask);
            }
        }
    }

    [DataContract]
    public abstract class ImmortalSerializerBase
    {
        [DataMember] public SerializableType[] KnownTypes;

        public abstract long SerializeSize(Immortal c);
        public abstract void Serialize(Immortal c, Stream writeToStream);
        public abstract Immortal Deserialize(Type runtimeType, Stream dataStream);
    }

    [DataContract]
    public abstract class Immortal<T> : Immortal
    {
        [DataMember]
        protected readonly T thisProxy;

        protected Immortal() : base()
        {
            // Can't do this here because GetProxy can be called only after deployment
            //this.thisProxy = GetProxy<T>(this.localAmbrosiaRuntime);
        }
    }

    [DataContract]
    public class AsyncContext
    {
        [DataMember]
        public long SequenceNumber { get; set; }
    }
}
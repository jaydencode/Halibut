using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization.Formatters;
using System.Security.Authentication;
using System.Text;
using Halibut.Diagnostics;
using Halibut.Protocol;
using Newtonsoft.Json;
using Newtonsoft.Json.Bson;

namespace Halibut.Services
{
    /// <summary>
    /// Implements the core message exchange protocol for both the client and server. 
    /// </summary>
    public class MessageExchangeProtocol
    {
        readonly ILog log;
        readonly MessageExchangeStream stream;
        bool identified;

        public MessageExchangeProtocol(Stream stream, ILog log)
        {
            this.log = log;
            this.stream = new MessageExchangeStream(stream, log);
        }

        public ResponseMessage ExchangeAsClient(RequestMessage request)
        {
            PrepareExchangeAsClient();

            stream.Send(request);
            return stream.Receive<ResponseMessage>();
        }

        void PrepareExchangeAsClient()
        {
            try
            {
                if (!identified)
                {
                    // First time connecting, so identify ourselves
                    stream.IdentifyAsClient();
                    identified = true;
                }

                stream.SendHello();
                stream.ExpectProceeed();
            }
            catch (Exception ex)
            {
                throw new ConnectionInitializationFailedException(ex);
            }
        }

        public int ExchangeAsSubscriber(Uri subscriptionId, Func<RequestMessage, ResponseMessage> incomingRequestProcessor)
        {
            // SEND: MX-SUBSCRIBER 1.0 [subid]
            // RECV: MX-SERVER 1.0
            // RECV: Request -> service invoker
            // SEND: Response
            // Repeat while request != null

            stream.IdentifyAsSubscriber(subscriptionId.ToString());
            var requestsProcessed = 0;
            while (ReceiveAndProcessRequest(stream, incomingRequestProcessor)) requestsProcessed++;
            return requestsProcessed;
        }

        static bool ReceiveAndProcessRequest(MessageExchangeStream stream, Func<RequestMessage, ResponseMessage> incomingRequestProcessor)
        {
            var request = stream.Receive<RequestMessage>();
            if (request == null) return false;
            var response = InvokeAndWrapAnyExceptions(request, incomingRequestProcessor);
            stream.Send(response);
            return true;
        }

        public void ExchangeAsServer(Func<RequestMessage, ResponseMessage> incomingRequestProcessor, Func<RemoteIdentity, IPendingRequestQueue> pendingRequests)
        {
            // RECV: <IDENTIFICATION>
            // SEND: MX-SERVER 1.0
            // IF MX-CLIENT
            //   RECV: Request
            //     call service invoker
            //   SEND: Response
            // ELSE
            //   while not empty
            //     Get next from queue
            //     SEND: Request
            //     RECV: Response

            var identity = stream.ReadRemoteIdentity();
            stream.IdentifyAsServer();
            switch (identity.IdentityType)
            {
                case RemoteIdentityType.Client:
                    ProcessClientRequests(incomingRequestProcessor);
                    break;
                case RemoteIdentityType.Subscriber:
                    ProcessSubscriber(pendingRequests(identity));
                    break;
                default:
                    throw new ProtocolException("Unexpected remote identity: " + identity.IdentityType);
            }
        }

        void ProcessClientRequests(Func<RequestMessage, ResponseMessage> incomingRequestProcessor)
        {
            while (true)
            {
                stream.ExpectHello();
                stream.SendProceed();

                var request = stream.Receive<RequestMessage>();
                var response = InvokeAndWrapAnyExceptions(request, incomingRequestProcessor);
                stream.Send(response);
            }
        }

        static ResponseMessage InvokeAndWrapAnyExceptions(RequestMessage request, Func<RequestMessage, ResponseMessage> incomingRequestProcessor)
        {
            try
            {
                return incomingRequestProcessor(request);
            }
            catch (Exception ex)
            {
                return ResponseMessage.FromException(request, ex.UnpackFromContainers());
            }
        }

        void ProcessSubscriber(IPendingRequestQueue pendingRequests)
        {
            while (true)
            {
                // TODO: Error handling
                var nextRequest = pendingRequests.Dequeue();

                stream.Send(nextRequest);
                if (nextRequest == null) 
                    break;

                var response = stream.Receive<ResponseMessage>();
                pendingRequests.ApplyResponse(response);
            }
        }
    }

    public class ConnectionInitializationFailedException : Exception
    {
        public ConnectionInitializationFailedException(string message) : base(message)
        {
        }

        public ConnectionInitializationFailedException(string message, Exception innerException)
            : base(message, innerException)
        {
        }

        public ConnectionInitializationFailedException(Exception innerException)
            : base(innerException.Message, innerException)
        {
        }
    }

    public class MessageExchangeStream
    {
        readonly Stream stream;
        readonly ILog log;
        readonly StreamWriter streamWriter;
        readonly StreamReader streamReader;
        readonly JsonSerializer serializer;
        readonly Version currentVersion = new Version(1, 0);

        public MessageExchangeStream(Stream stream, ILog log)
        {
            this.stream = stream;
            this.log = log;
            streamWriter = new StreamWriter(stream, new UTF8Encoding(false));
            streamReader = new StreamReader(stream, new UTF8Encoding(false));
            serializer = Serializer();
        }

        public static Func<JsonSerializer> Serializer = CreateDefault;

        public void IdentifyAsClient()
        {
            log.Write(EventType.Diagnostic, "Identifying as a client");
            streamWriter.Write("MX-CLIENT ");
            streamWriter.Write(currentVersion);
            streamWriter.WriteLine();
            streamWriter.WriteLine();
            streamWriter.Flush();

            ExpectServerIdentity();
        }

        public void SendHello()
        {
            streamWriter.Write("HELLO");
            streamWriter.WriteLine();
            streamWriter.Flush();
        }

        public void SendProceed()
        {
            streamWriter.Write("PROCEED");
            streamWriter.WriteLine();
            streamWriter.Flush();
        }

        public void ExpectHello()
        {
            var line = ReadLine();
            if (line != "HELLO")
                throw new ProtocolException("Expected a HELLO line, got: " + line);
        }

        public void ExpectProceeed()
        {
            var line = ReadLine();
            if (line == null)
                throw new AuthenticationException("XYZ");
            if (line != "PROCEED")
                throw new ProtocolException("Expected a HELLO line, got: " + line);
        }

        public string ReadLine()
        {
            var line = streamReader.ReadLine();
            while (line == string.Empty)
            {
                line = streamReader.ReadLine();
            }

            return line;
        }

        public void IdentifyAsSubscriber(string subscriptionId)
        {
            streamWriter.Write("MX-SUBSCRIBER ");
            streamWriter.Write(currentVersion);
            streamWriter.Write(" ");
            streamWriter.Write(subscriptionId);
            streamWriter.WriteLine();
            streamWriter.WriteLine();
            streamWriter.Flush();

            ExpectServerIdentity();
        }

        public void IdentifyAsServer()
        {
            streamWriter.Write("MX-SERVER ");
            streamWriter.Write(currentVersion);
            streamWriter.WriteLine();
            streamWriter.WriteLine();
            streamWriter.Flush();
        }

        public RemoteIdentity ReadRemoteIdentity()
        {
            var line = streamReader.ReadLine();
            if (string.IsNullOrEmpty(line)) throw new ProtocolException("Unable to receive the remote identity; the identity line was empty.");
            var parts = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            var identityType = ParseIdentityType(parts[0]);
            if (identityType == RemoteIdentityType.Subscriber)
            {
                if (parts.Length < 3) throw new ProtocolException("Unable to receive the remote identity; the client identified as a subscriber, but did not supply a subscription ID.");
                var subscriptionId = new Uri(parts[2]);
                return new RemoteIdentity(identityType, subscriptionId);
            }
            return new RemoteIdentity(identityType);
        }

        public void Send<T>(T message)
        {
            using (var capture = StreamCapture.New())
            {
                WriteBsonMessage(message);
                WriteEachStream(capture.SerializedStreams);
            }
        }

        public T Receive<T>()
        {
            using (var capture = StreamCapture.New())
            {
                var result = ReadBsonMessage<T>();
                ReadStreams(capture);
                return result;
            }
        }

        static JsonSerializer CreateDefault()
        {
            var serializer = JsonSerializer.Create();
            serializer.Formatting = Formatting.None;
            serializer.ContractResolver = new HalibutContractResolver();
            serializer.TypeNameHandling = TypeNameHandling.Auto;
            serializer.TypeNameAssemblyFormat = FormatterAssemblyStyle.Simple;
            serializer.DateFormatHandling = DateFormatHandling.IsoDateFormat;
            return serializer;
        }

        static RemoteIdentityType ParseIdentityType(string identityType)
        {
            switch (identityType)
            {
                case "MX-CLIENT":
                    return RemoteIdentityType.Client;
                case "MX-SERVER":
                    return RemoteIdentityType.Server;
                case "MX-SUBSCRIBER":
                    return RemoteIdentityType.Subscriber;
                default:
                    throw new ProtocolException("Unable to process remote identity; unknown identity type: '" + identityType + "'");
            }
        }

        void ExpectServerIdentity()
        {
            var identity = ReadRemoteIdentity();
            if (identity.IdentityType != RemoteIdentityType.Server)
                throw new ProtocolException("Expected the remote endpoint to identity as a server. Instead, it identified as: " + identity.IdentityType);
        }

        T ReadBsonMessage<T>()
        {
            using (var zip = new DeflateStream(stream, CompressionMode.Decompress, true))
            using (var buffer = new BufferedStream(zip, 8192))
            using (var bson = new BsonReader(buffer) { CloseInput = false })
            {
                return (T)serializer.Deserialize<MessageEnvelope>(bson).Message;
            }
        }

        void ReadStreams(StreamCapture capture)
        {
            var expected = capture.DeserializedStreams.Count;

            for (var i = 0; i < expected; i++)
            {
                ReadStream(capture);
            }
        }

        void ReadStream(StreamCapture capture)
        {
            var reader = new BinaryReader(stream);
            var id = new Guid(reader.ReadBytes(16));
            var length = reader.ReadInt64();
            var dataStream = FindStreamById(capture, id);
            var tempFile = CopyStreamToFile(id, length, reader);
            dataStream.Attach(tempFile.ReadAndDelete);
        }

        static TemporaryFileStream CopyStreamToFile(Guid id, long length, BinaryReader reader)
        {
            var path = Path.Combine(Path.GetTempPath(), id.ToString());
            using (var fileStream = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                var buffer = new byte[1024*128];
                while (length > 0)
                {
                    var read = reader.Read(buffer, 0, (int) Math.Min(buffer.Length, length));
                    length -= read;
                    fileStream.Write(buffer, 0, read);
                }
            }
            return new TemporaryFileStream(path);
        }

        static DataStream FindStreamById(StreamCapture capture, Guid id)
        {
            var dataStream = capture.DeserializedStreams.FirstOrDefault(d => d.Id == id);
            if (dataStream == null) throw new Exception("Unexpected stream!");
            return dataStream;
        }

        void WriteBsonMessage<T>(T messages)
        {
            using (var zip = new DeflateStream(stream, CompressionMode.Compress, true))
            using (var buffer = new BufferedStream(zip))
            using (var bson = new BsonWriter(buffer) {CloseOutput = false})
            {
                serializer.Serialize(bson, new MessageEnvelope { Message = messages });
                bson.Flush();
            }
        }

        void WriteEachStream(IEnumerable<DataStream> streams)
        {
            foreach (var dataStream in streams)
            {
                var writer = new BinaryWriter(stream);
                writer.Write(dataStream.Id.ToByteArray());
                writer.Write(dataStream.Length);
                writer.Flush();

                var buffer = new BufferedStream(stream, 8192);
                dataStream.Write(buffer);
                buffer.Flush();
            }
        }

        class TemporaryFileStream
        {
            readonly string path;
            bool deleted;

            public TemporaryFileStream(string path)
            {
                this.path = path;
            }

            public void ReadAndDelete(Action<Stream> callback)
            {
                if (deleted) throw new InvalidOperationException("This stream has already been received once, and it cannot be read again.");
                deleted = true;
                Read(callback);
                Delete();
            }

            void Read(Action<Stream> callback)
            {
                using (var file = new FileStream(path, FileMode.Open, FileAccess.Read))
                {
                    callback(file);
                }
            }

            void Delete()
            {
                File.Delete(path);
            }
        }

        class MessageEnvelope
        {
            public object Message { get; set; }
        }
    }
}
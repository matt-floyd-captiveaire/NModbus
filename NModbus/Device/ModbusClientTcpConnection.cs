﻿using NModbus.IO;
using NModbus.Logging;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace NModbus.Device
{
    using Extensions;

    /// <summary>
    /// Represents an incoming connection from a Modbus master. Contains the slave's logic to process the connection.
    /// </summary>
    [Obsolete("Master/Slave terminology is deprecated and replaced with Client/Server. Use ModbusClientTcpConnection instead.")]
    internal class ModbusMasterTcpConnection : ModbusClientTcpConnection
    {
        public ModbusMasterTcpConnection(TcpClient client, IModbusSlaveNetwork slaveNetwork, IModbusFactory modbusFactory, IModbusLogger logger) : base(client, slaveNetwork, modbusFactory, logger) { }
    }

    /// <summary>
    /// Represents an incoming connection from a Modbus client. Contains the server's logic to process the connection.
    /// </summary>
    internal class ModbusClientTcpConnection : ModbusDevice, IDisposable
    {
        
        private readonly TcpClient _client;
        private readonly string _endPoint;
        private readonly Stream _stream;
        private readonly IModbusServerNetwork _serverNetwork;
        private readonly IModbusFactory _modbusFactory;
        private readonly Task _requestHandlerTask;

        private readonly byte[] _mbapHeader = new byte[6];
        private byte[] _messageFrame;

        public ModbusClientTcpConnection(TcpClient client, IModbusServerNetwork serverNetwork, IModbusFactory modbusFactory, IModbusLogger logger)
            : base(new ModbusIpTransport(new TcpClientAdapter(client), modbusFactory, logger))
        {
            Logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _client = client ?? throw new ArgumentNullException(nameof(client));
            _endPoint = client.Client.RemoteEndPoint.ToString();
            _stream = client.GetStream();
            _serverNetwork = serverNetwork ?? throw new ArgumentNullException(nameof(serverNetwork));
            _modbusFactory = modbusFactory ?? throw new ArgumentNullException(nameof(modbusFactory));
            _requestHandlerTask = Task.Run((Func<Task>)HandleRequestAsync);
        }

        /// <summary>
        ///     Occurs when a Modbus client TCP connection is closed.
        /// </summary>
        public event EventHandler<TcpConnectionEventArgs> ModbusClientTcpConnectionClosed;

        public IModbusLogger Logger { get; }

        public string EndPoint => _endPoint;

        public Stream Stream => _stream;

        public TcpClient TcpClient => _client;

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _stream.Dispose();
            }

            base.Dispose(disposing);
        }

        private async Task HandleRequestAsync()
        {
            while (true)
            {
                Logger.Debug($"Begin reading header from Client at IP: {EndPoint}");

                int readBytes = await Stream.ReadAsync(_mbapHeader, 0, 6).ConfigureAwait(false);
                if (readBytes == 0)
                {
                    Logger.Debug($"0 bytes read, Client at {EndPoint} has closed Socket connection.");
                    ModbusClientTcpConnectionClosed?.Invoke(this, new TcpConnectionEventArgs(EndPoint));
                    return;
                }

                ushort frameLength = (ushort)IPAddress.HostToNetworkOrder(BitConverter.ToInt16(_mbapHeader, 4));
                Logger.Debug($"Client at {EndPoint} sent header: \"{string.Join(", ", _mbapHeader)}\" with {frameLength} bytes in PDU");

                _messageFrame = new byte[frameLength];
                readBytes = await Stream.ReadAsync(_messageFrame, 0, frameLength).ConfigureAwait(false);
                if (readBytes == 0)
                {
                    Logger.Debug($"0 bytes read, Client at {EndPoint} has closed Socket connection.");
                    ModbusClientTcpConnectionClosed?.Invoke(this, new TcpConnectionEventArgs(EndPoint));
                    return;
                }

                Logger.Debug($"Read frame from Client at {EndPoint} completed {readBytes} bytes");
                byte[] frame = _mbapHeader.Concat(_messageFrame).ToArray();
                Logger.Trace($"RX from Client at {EndPoint}: {string.Join(", ", frame)}");

                var request = _modbusFactory.CreateModbusRequest(_messageFrame);
                request.TransactionId = (ushort)IPAddress.NetworkToHostOrder(BitConverter.ToInt16(frame, 0));

                IModbusServer server = _serverNetwork.GetServer(request.ServerAddress);

                if (server != null)
                {
                    //TODO: Determine if this is appropriate

                    // perform action and build response
                    IModbusMessage response = server.ApplyRequest(request);
                    response.TransactionId = request.TransactionId;

                    // write response
                    byte[] responseFrame = Transport.BuildMessageFrame(response);
                    Logger.Information($"TX to Client at {EndPoint}: {string.Join(", ", responseFrame)}");
                    await Stream.WriteAsync(responseFrame, 0, responseFrame.Length).ConfigureAwait(false);
                }
            }
        }
    }
}

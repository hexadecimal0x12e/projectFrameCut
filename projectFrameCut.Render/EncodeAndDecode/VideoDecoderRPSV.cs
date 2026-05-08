using projectFrameCut.Render.RenderAPIBase.Sources;
using projectFrameCut.Shared;
using System;
using System.Collections.Generic;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace projectFrameCut.Render.EncodeAndDecode
{
    internal class RawPictureSequenceStreamVideoDecoderContext : IVideoSource
    {
        private bool _disposed;

        public int? ResultBitPerPixel => BytesPerPixel switch
        {
            BytesPerPixelMode.Byte => 8,
            _ => 16 //we'll convert 32-bit data to 16-bit, so the result is always 16-bit or 8-bit.
        };

        public string[] PreferredExtension => [];

        public uint Index { get; set; } = 0;

        public long TotalFrames => -1;

        public double Fps => 0;

        public int Width => 0;

        public int Height => 0;

        public bool Disposed => _disposed;

        public bool EnableLock { get; set; }

        public bool StrictMode { get; set; } = true;
        
        public string TypeName => "RawPictureSequenceStreamVideoDecoderContext";

        #region Receive Mode Configuration

        /// <summary>
        /// Specifies the bytes per pixel format for receiving data.
        /// </summary>
        public enum BytesPerPixelMode : byte
        {
            /// <summary>8-bit per pixel (values 0-255)</summary>
            Byte = 0b00,
            /// <summary>16-bit per pixel (values 0-65535)</summary>
            UShort = 0b01,
            /// <summary>32-bit per pixel (values 0-4294967295)</summary>
            UInt = 0b10,
        }

        /// <summary>
        /// Specifies the alpha channel format for receiving data.
        /// </summary>
        public enum AlphaModeType : byte
        {
            /// <summary>No alpha channel</summary>
            None = 0b000,
            /// <summary>8-bit alpha channel</summary>
            Alpha8Bit = 0b001,
            /// <summary>16-bit alpha channel</summary>
            Alpha16Bit = 0b010,
            /// <summary>32-bit alpha channel</summary>
            Alpha32Bit = 0b011,
            /// <summary>16-bit float alpha channel</summary>
            AlphaFloat16 = 0b110,
            /// <summary>32-bit float alpha channel</summary>
            AlphaFloat32 = 0b111
        }

        /// <summary>
        /// Gets or sets the bytes per pixel mode.
        /// </summary>
        public BytesPerPixelMode BytesPerPixel
        {
            get => (BytesPerPixelMode)(_currentReceiveMode & 0b11);
            set => _currentReceiveMode = (byte)((_currentReceiveMode & 0b11111100) | ((byte)value & 0b11));
        }

        /// <summary>
        /// Gets or sets the alpha channel mode.
        /// </summary>
        public AlphaModeType AlphaMode
        {
            get => (AlphaModeType)((_currentReceiveMode >> 2) & 0b111);
            set => _currentReceiveMode = (byte)((_currentReceiveMode & 0b11100011) | (((byte)value & 0b111) << 2));
        }

        /// <summary>
        /// Gets or sets whether to enable checksum verification for received data.
        /// </summary>
        public bool EnableChecksum
        {
            get => ((_currentReceiveMode >> 5) & 0b1) == 1;
            set => _currentReceiveMode = (byte)((_currentReceiveMode & 0b11011111) | ((value ? 1 : 0) << 5));
        }

        /// <summary>
        /// Gets the current raw receive mode byte value.
        /// </summary>
        public byte CurrentReceiveModeByte => _currentReceiveMode;

        /// <summary>
        /// Sets the entire receive mode byte directly.
        /// </summary>
        /// <param name="mode">The raw receive mode byte value</param>
        public void SetReceiveMode(byte mode)
        {
            _currentReceiveMode = mode;
        }

        #endregion

        TcpClient? _tcpReceiver;
        NamedPipeClientStream? _pipeServer;
        bool isTcp = false, isPiped = false;
        private NetworkStream? _tcpStream;
        private byte _currentReceiveMode = 0;
        private string? _tcpHost;
        private int _tcpPort;

        public RawPictureSequenceStreamVideoDecoderContext(string newSource)
        {
            if (newSource is null) return;

            var pathParts = newSource.Split('@', 2, StringSplitOptions.TrimEntries);
            var protocol = pathParts[0];
            var path = pathParts[1];
            switch (protocol)
            {
                case "tcp":
                    {
                        isTcp = true;
                        var uri = new Uri(path);
                        _tcpHost = uri.Host;
                        _tcpPort = uri.Port;
                        _tcpReceiver = new();
                        _pipeServer = null;
                        break;
                    }
                case "pipe":
                    {
                        isPiped = true;
                        _pipeServer = new NamedPipeClientStream(".", path);
                        _tcpReceiver = null;
                        break;
                    }
                default:
                    {
                        _tcpReceiver = null;
                        _pipeServer = null;
                        throw new NotSupportedException($"Unsupported type of protocol {protocol}.");
                    }
            }
        }

        public IVideoSource CreateNew(string newSource)
        {
            return new RawPictureSequenceStreamVideoDecoderContext(newSource);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
        }

        public IPicture GetFrame(uint targetFrame, bool hasAlpha = false)
        {
            ObjectDisposedException.ThrowIf(_disposed, typeof(RawPictureSequenceStreamVideoDecoderContext));


            Stream? stream = null;
            if (isTcp && _tcpStream != null)
            {
                stream = _tcpStream;
            }
            else if (isPiped && _pipeServer != null)
            {
                stream = _pipeServer;
            }

            if (stream == null || !stream.CanRead)
                throw new InvalidOperationException("Stream is not available or not readable.");

            // Extract receive mode settings
            byte bppBits = (byte)(_currentReceiveMode & 0b11);        // Bits 0-1
            byte alphaBits = (byte)((_currentReceiveMode >> 2) & 0b111); // Bits 2-4
            bool enableChecksum = ((_currentReceiveMode >> 5) & 0b1) == 1; // Bit 5

            // Build request
            var request = new RPSVRequestStruct
            {
                HeaderMagic = 0x0E,
                Version = 1,
                Mode = 0, // On-Demand mode
                Amount = targetFrame,
                ReceiveMode = _currentReceiveMode
            };

            // Send request
            byte[] requestBytes = StructToBytes(request);
            stream.Write(requestBytes, 0, requestBytes.Length);
            stream.Flush();

            // Receive response header
            byte[] headerBuffer = new byte[13]; // Size of RPSVMessageStruct header
            int bytesRead = stream.Read(headerBuffer, 0, headerBuffer.Length);
            if (bytesRead != headerBuffer.Length)
                throw new InvalidOperationException("Failed to read complete response header.");

            var response = BytesToRPSVMessage(headerBuffer);

            if (response.HeaderMagic != 0x0F)
                throw new InvalidOperationException("Invalid response header magic.");

            if (response.StatusCode != 0)
                throw new InvalidOperationException($"Server returned error code: {response.StatusCode}");

            // Calculate bytes per pixel
            int bytesPerPixel = bppBits switch
            {
                0b00 => 1,  // 8-bit
                0b01 => 2,  // 16-bit
                0b10 => 4,  // 32-bit
                _ => throw new NotSupportedException($"Unsupported bytes per pixel bits: {bppBits}")
            };

            // Calculate alpha channel size if present
            int alphaSize = alphaBits switch
            {
                0b000 => 0,      // No alpha
                0b001 => 1,      // 8-bit alpha
                0b010 => 2,      // 16-bit alpha
                0b011 => 4,      // 32-bit alpha
                0b110 => 2,      // 16-bit float alpha
                0b111 => 4,      // 32-bit float alpha
                _ => throw new NotSupportedException($"Unsupported alpha mode: {alphaBits}")
            };

            int totalPixels = response.FrameWidth * response.FrameHeight;
            int pixelDataSize = totalPixels * (bytesPerPixel * 3 + alphaSize); // RGB + optional Alpha
            int checkSumSize = enableChecksum ? 16 : 0; // MD5 is 16 bytes

            // Read pixel data and checksum
            byte[] pixelData = new byte[pixelDataSize + checkSumSize];
            bytesRead = 0;
            while (bytesRead < pixelData.Length)
            {
                int read = stream.Read(pixelData, bytesRead, pixelData.Length - bytesRead);
                if (read == 0)
                    throw new InvalidOperationException("Connection closed while reading pixel data.");
                bytesRead += read;
            }

            // Verify checksum if enabled
            if (StrictMode && enableChecksum)
            {
                byte[] receivedChecksum = new byte[16];
                Array.Copy(pixelData, pixelDataSize, receivedChecksum, 0, 16);

                byte[] actualData = new byte[pixelDataSize];
                Array.Copy(pixelData, actualData, pixelDataSize);
                byte[] calculatedChecksum = MD5.HashData(actualData);
                if (!AreByteArraysEqual(calculatedChecksum, receivedChecksum))
                    throw new InvalidOperationException("Checksum verification failed.");
            }
            if (bytesPerPixel == 1)
            {
                return CreatePictureFromData8bpp(pixelData, response.FrameWidth, response.FrameHeight, bytesPerPixel, alphaBits, pixelDataSize, response.FrameNumber, hasAlpha);

            }
            else
            {
                return CreatePictureFromData16bpp(pixelData, response.FrameWidth, response.FrameHeight, bytesPerPixel, alphaBits, pixelDataSize, response.FrameNumber, hasAlpha);


            }
            // Parse pixel data and create Picture
        }

        public void Initialize()
        {

            try
            {
                if (isTcp)
                {
                    if (_tcpReceiver == null || _tcpHost == null)
                        throw new InvalidOperationException("TCP connection not properly initialized.");

                    _tcpReceiver.Connect(_tcpHost, _tcpPort);
                    _tcpStream = _tcpReceiver.GetStream();
                }
                else if (isPiped)
                {
                    if (_pipeServer == null)
                        throw new InvalidOperationException("Pipe connection not properly initialized.");

                    _pipeServer.Connect();
                }
                else
                {
                    return; // No connection protocol specified, just return without throwing to allow for TryInitialize to work.
                    //throw new InvalidOperationException("No valid connection protocol specified.");
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("Failed to initialize RPSV connection.", ex);
            }
        }


        private byte[] StructToBytes(RPSVRequestStruct request)
        {
            byte[] buffer = new byte[9];
            buffer[0] = request.HeaderMagic;
            buffer[1] = request.Version;
            buffer[2] = request.Mode;
            Buffer.BlockCopy(BitConverter.GetBytes(request.Amount), 0, buffer, 3, 4);
            buffer[8] = request.ReceiveMode;
            return buffer;
        }

        private RPSVMessageStruct BytesToRPSVMessage(byte[] buffer)
        {
            if(buffer.Length >= 3)
            {
                var status = buffer[2];
                if (status != 0)
                {
                    throw new InvalidOperationException($"A error occurs while processing this frame request. Err code:{status}");
                }
            }

            if (buffer.Length < 13)
                throw new ArgumentException("Buffer too small for RPSVMessageStruct");

            return new RPSVMessageStruct
            {
                HeaderMagic = buffer[0],
                Version = buffer[1],
                StatusCode = buffer[2],
                FrameWidth = BitConverter.ToUInt16(buffer, 3),
                FrameHeight = BitConverter.ToUInt16(buffer, 5),
                FrameNumber = BitConverter.ToUInt32(buffer, 7)
            };
        }

        private bool AreByteArraysEqual(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++)
            {
                if (a[i] != b[i]) return false;
            }
            return true;
        }

        private Picture16bpp CreatePictureFromData16bpp(byte[] pixelData, ushort width, ushort height,
                                               int bytesPerPixel, byte alphaBits, int pixelDataSize,
                                               uint frameNumber, bool hasAlpha)
        {
            int totalPixels = width * height;
            var picture = new Picture16bpp(width, height)
            {
                frameIndex = frameNumber
            };

            int offset = 0;

            for (int i = 0; i < totalPixels; i++)
            {
                // Read R channel
                picture.r[i] = ReadColorValue16bpp(pixelData, offset, bytesPerPixel);
                offset += bytesPerPixel;

                // Read G channel
                picture.g[i] = ReadColorValue16bpp(pixelData, offset, bytesPerPixel);
                offset += bytesPerPixel;

                // Read B channel
                picture.b[i] = ReadColorValue16bpp(pixelData, offset, bytesPerPixel);
                offset += bytesPerPixel;
            }

            // Read alpha channel if present
            if (alphaBits != 0b000)
            {
                picture.a = new float[totalPixels];
                picture.hasAlphaChannel = true;

                for (int i = 0; i < totalPixels; i++)
                {
                    picture.a[i] = ReadAlphaValue(pixelData, offset, alphaBits);
                    offset += GetAlphaSize(alphaBits);
                }
            }

            return picture;
        }

        private ushort ReadColorValue16bpp(byte[] data, int offset, int bytesPerPixel)
        {
            return bytesPerPixel switch
            {
                2 => BitConverter.ToUInt16(data, offset),
                4 => (ushort)(BitConverter.ToUInt32(data, offset) >> 16), // 32-bit -> 16-bit
                _ => throw new NotSupportedException($"Unsupported bytes per pixel: {bytesPerPixel}")
            };
        }
        private Picture8bpp CreatePictureFromData8bpp(byte[] pixelData, ushort width, ushort height,
                                               int bytesPerPixel, byte alphaBits, int pixelDataSize,
                                               uint frameNumber, bool hasAlpha)
        {
            int totalPixels = width * height;
            var picture = new Picture8bpp(width, height)
            {
                frameIndex = frameNumber
            };

            int offset = 0;

            for (int i = 0; i < totalPixels; i++)
            {
                // Read R channel
                picture.r[i] = pixelData[offset];
                offset += bytesPerPixel;

                // Read G channel
                picture.g[i] = pixelData[offset];
                offset += bytesPerPixel;

                // Read B channel
                picture.b[i] = pixelData[offset];
                offset += bytesPerPixel;
            }

            // Read alpha channel if present
            if (alphaBits != 0b000)
            {
                picture.a = new float[totalPixels];
                picture.hasAlphaChannel = true;

                for (int i = 0; i < totalPixels; i++)
                {
                    picture.a[i] = ReadAlphaValue(pixelData, offset, alphaBits);
                    offset += GetAlphaSize(alphaBits);
                }
            }

            return picture;
        }


        private float ReadAlphaValue(byte[] data, int offset, byte alphaBits)
        {
            return alphaBits switch
            {
                0b001 => data[offset] / 255f,  // 8-bit to 0..1
                0b010 => BitConverter.ToUInt16(data, offset) / 65535f,  // 16-bit to 0..1
                0b011 => BitConverter.ToUInt32(data, offset) / 4294967295f,  // 32-bit to 0..1
                0b110 => (float)BitConverter.ToHalf(data, offset),  // 16-bit float
                0b111 => BitConverter.ToSingle(data, offset),  // 32-bit float
                _ => throw new NotSupportedException($"Unsupported alpha mode: {alphaBits}")
            };
        }

        private int GetAlphaSize(byte alphaBits)
        {
            return alphaBits switch
            {
                0b001 => 1,
                0b010 => 2,
                0b011 => 4,
                0b110 => 2,
                0b111 => 4,
                _ => 0
            };
        }

        public struct RPSVRequestStruct
        {
            /// <summary>
            /// Equals to 00001110
            /// </summary>
            public byte HeaderMagic;
            /// <summary>
            /// Equals to 1 in this version.
            /// </summary>
            public byte Version;
            /// <summary>
            /// Determine the mode of receiving.
            /// 0 for On-Demand (1 frame at 1 time) and 1 for Continually provide <see cref="Amount"/> number of frame(s)
            /// </summary>
            public byte Mode;
            /// <summary>
            /// When Mode is 0, specifics the Index of the frame get. 
            /// when Mode is 1, specifics the amount of frame you want. Use <see cref="uint.MaxValue"/> to continually provide until reach the end of source, or have to be stopped for any reason.
            /// </summary>
            public uint Amount;
            /// <summary>
            /// Use first 2 bit to determine Byte-Per-Pixel
            /// (00: 8 bit, 01: 16bit, 10: 32 bit, 11: Reversed)
            /// then 3 bit to determine Alpha mode 
            /// (000:no alpha, 001: 8bit alpha, 010: 16bit alpha, 011: 32bit alpha, 110: 16bit-float unsigned alpha, 111: 32bit-float unsigned alpha)
            /// then 1 bit to control checksum 
            /// (0: off, 1: on)
            /// </summary>
            public byte ReceiveMode;
        }

        /// <summary>
        /// The structure of the RPSV Frame Data.
        /// </summary>
        public struct RPSVMessageStruct
        {
            /// <summary>
            /// Equals to 00001111
            /// </summary>
            public byte HeaderMagic;
            /// <summary>
            /// Equals to 1 in this version.
            /// </summary>
            public byte Version;
            /// <summary>
            /// 0 for success, 1 for frame not found, 2 for unsupported format. 
            /// Other codes larger than 128 can be used to show a custom error.
            /// </summary>
            public byte StatusCode;
            /// <summary>
            /// Width of result frame
            /// </summary>
            public ushort FrameWidth;
            /// <summary>
            /// Height of result frame
            /// </summary>
            public ushort FrameHeight;
            /// <summary>
            /// The frame number of this message body includes.
            /// </summary>
            public uint FrameNumber;

            //After that, the frame's Data will be transmitted by R-G-B-A(if alpha on) or R-G-B
            //Then comes up with a MD5 checksum of the pixel data(s) if checksum is on.
            //If the stream mode is on, the header will be sent again along with the new data.
        }

    }
}
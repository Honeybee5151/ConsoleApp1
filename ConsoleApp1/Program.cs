using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using System.Runtime.InteropServices;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Collections.Concurrent;
using NAudio.Lame;
using System.IO;
using Concentus.Structs;
using Concentus.Enums;
using System.Net;

namespace ConsoleApp1
{
    // Data structures for communication
    public class MicrophoneInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public bool IsDefault { get; set; }
        public bool IsEnabled { get; set; }
    }

    public class AudioData
    {
        public float Level { get; set; }
        public float Peak { get; set; }
        public bool IsActive { get; set; }
        public DateTime Timestamp { get; set; }
    }

    // UDP Authentication structures
    public class UdpAuthRequest
    {
        public string PlayerId { get; set; }
        public string VoiceId { get; set; }
        public string Command { get; set; } = "AUTH";
    }

    public class UdpPriorityCommand
    {
        public string PlayerId { get; set; }
        public string SettingType { get; set; }
        public string Value { get; set; }
        public string Command { get; set; } = "PRIORITY";
    }

    public class OpusAudioProcessor
    {
        // REQUIRED: Field declarations at class level
        private OpusEncoder encoder;
        private OpusDecoder decoder;
        private const int SAMPLE_RATE = 48000; // Opus works best at 48kHz
        private const int CHANNELS = 1;
        private const int FRAME_SIZE = 960; // 20ms at 48kHz

        public OpusAudioProcessor()
        {
            try
            {
                encoder = new OpusEncoder(SAMPLE_RATE, CHANNELS, OpusApplication.OPUS_APPLICATION_VOIP);
                decoder = new OpusDecoder(SAMPLE_RATE, CHANNELS);

                // Only set properties that actually exist
                encoder.Bitrate = 24000;
                encoder.Complexity = 5;
            
                Console.WriteLine("OpusAudioProcessor initialized successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Failed to initialize Opus: {ex.Message}");
                encoder = null;
                decoder = null;
            }
        }
        
        public byte[] EncodeToOpus(byte[] rawPcmData)
        {
            if (encoder == null)
            {
                Console.WriteLine("Opus encoder not available, returning raw PCM");
                return rawPcmData;
            }

            try
            {
                // Convert byte array to short array (16-bit PCM)
                short[] pcmSamples = new short[rawPcmData.Length / 2];
                Buffer.BlockCopy(rawPcmData, 0, pcmSamples, 0, rawPcmData.Length);

                // Opus output buffer
                byte[] opusOutput = new byte[4000]; // Max Opus packet size

                // CORRECTED: Use proper Encode method signature
                int encodedBytes = encoder.Encode(pcmSamples, 0, pcmSamples.Length, opusOutput, 0, opusOutput.Length);

                if (encodedBytes < 0)
                {
                    Console.WriteLine($"Opus encoding failed with error code: {encodedBytes}");
                    return rawPcmData;
                }

                // Return only the used bytes
                byte[] result = new byte[encodedBytes];
                Array.Copy(opusOutput, 0, result, 0, encodedBytes);

                Console.WriteLine($"Opus: Encoded {rawPcmData.Length} PCM bytes → {encodedBytes} Opus bytes");
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Opus encoding error: {ex.Message}");
                return rawPcmData; // Fallback to raw PCM
            }
        }

        public byte[] DecodeFromOpus(byte[] opusData)
        {
            if (decoder == null)
            {
                Console.WriteLine("Opus decoder not available, returning original data");
                return opusData;
            }

            try
            {
                // Decode buffer (enough for 20ms at 48kHz)
                short[] pcmOutput = new short[FRAME_SIZE * CHANNELS];

                // CORRECTED: Use proper Decode method signature
                int decodedSamples = decoder.Decode(opusData, 0, opusData.Length, pcmOutput, 0, pcmOutput.Length, false);

                if (decodedSamples < 0)
                {
                    Console.WriteLine($"Opus decoding failed with error code: {decodedSamples}");
                    return new byte[0];
                }

                // Convert short array back to byte array
                byte[] result = new byte[decodedSamples * 2]; // 2 bytes per sample
                Buffer.BlockCopy(pcmOutput, 0, result, 0, result.Length);

                Console.WriteLine($"Opus: Decoded {opusData.Length} Opus bytes → {result.Length} PCM bytes");
                return result;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Opus decoding error: {ex.Message}");
                return new byte[0]; // Return empty on error
            }
        }
    }

    // UDP-ONLY Proximity Chat Manager
    public class ProximityChatManager : IDisposable
    {
        // Inner classes for UDP communication
        public class UdpAuthRequest
        {
            public string PlayerId { get; set; }
            public string VoiceId { get; set; }
            public string Command { get; set; } = "AUTH";
        }

        public class UdpPriorityCommand
        {
            public string PlayerId { get; set; }
            public string SettingType { get; set; }
            public string Value { get; set; }
            public string Command { get; set; } = "PRIORITY";
        }

        #region Fields

        private WaveInEvent waveIn;
        private MMDeviceEnumerator deviceEnumerator;
        private MMDevice selectedDevice;
        private List<MicrophoneInfo> availableMicrophones;
        private OpusAudioProcessor opusProcessor;
        private VoiceManager voiceManager;
        private DateTime lastAudioSent = DateTime.MinValue;
        private const int AUDIO_SEND_INTERVAL_MS = 50; // Faster for UDP

        // Audio processing
        private volatile float currentLevel;
        private volatile float peakLevel;
        private volatile float smoothedLevel;
        private int audioUpdateCounter = 0;
        private const int AUDIO_UPDATE_SKIP = 50;

        // UDP Communication
        private UdpClient udpClient;
        private IPEndPoint serverEndpoint;
        private volatile bool isConnectedToServer;
        private string serverId;
        private string storedVoiceId;
        private bool isAuthenticated = false;

        // Audio queue for UDP
        private readonly ConcurrentQueue<byte[]> outgoingOpusData = new ConcurrentQueue<byte[]>();
        private Task udpSenderTask;
        private readonly CancellationTokenSource udpCancellation = new CancellationTokenSource();

        // ActionScript communication
        private ActionScriptBridge actionScriptBridge;

        // Settings
        public bool IsMicrophoneEnabled { get; private set; }
        public string SelectedMicrophoneId { get; private set; }
        public float MicrophoneSensitivity { get; set; } = 1.0f;
        public float NoiseGate { get; set; } = 0.005f; // Lower for better sensitivity

        public bool allowAudioTransmission = true;

        // Connection management
        public event EventHandler<string> ConnectionStatusChanged;
        private DateTime lastConnectionCheck = DateTime.Now;
        private readonly object reconnectionLock = new object();
        private bool isReconnecting = false;
        private string storedServerAddress;
        private int storedPort;

        #endregion

        #region Constructor and Initialization

        public ProximityChatManager()
        {
            deviceEnumerator = new MMDeviceEnumerator();
            availableMicrophones = new List<MicrophoneInfo>();
            actionScriptBridge = new ActionScriptBridge();
            opusProcessor = new OpusAudioProcessor();
            currentLevel = 0f;
            peakLevel = 0f;
            smoothedLevel = 0f;

            RefreshMicrophones();
        }

        #endregion

        #region UDP Communication

        private async Task UdpSenderWorker()
        {
            Console.WriteLine("[UDP_SENDER] Starting UDP sender worker");
            
            while (!udpCancellation.Token.IsCancellationRequested)
            {
                try
                {
                    // Connection health check
                    var now = DateTime.Now;
                    if ((now - lastConnectionCheck).TotalSeconds >= 30)
                    {
                        await SendPingToServer();
                        lastConnectionCheck = now;
                    }

                    // Send audio data
                    if (outgoingOpusData.TryDequeue(out var opusData) && isConnectedToServer && isAuthenticated)
                    {
                        await SendVoiceDataUdp(opusData);
                    }

                    await Task.Delay(5, udpCancellation.Token); // Very frequent for low latency
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[UDP_SENDER] Error: {ex.Message}");
                    await Task.Delay(100, udpCancellation.Token);
                }
            }
            
            Console.WriteLine("[UDP_SENDER] UDP sender worker stopped");
        }

        private async Task SendVoiceDataUdp(byte[] opusData)
        {
            try
            {
                // Create voice packet: [16 bytes playerId][Opus audio data]
                byte[] voicePacket = new byte[16 + opusData.Length];
                
                // Player ID (16 bytes, padded)
                byte[] playerIdBytes = Encoding.UTF8.GetBytes(serverId.PadRight(16));
                Array.Copy(playerIdBytes, 0, voicePacket, 0, 16);
                
                // Opus audio data
                Array.Copy(opusData, 0, voicePacket, 16, opusData.Length);
                
                // Send via UDP
                await udpClient.SendAsync(voicePacket, voicePacket.Length, serverEndpoint);
                
                Console.WriteLine($"[UDP_VOICE] Sent {opusData.Length} Opus bytes to server");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UDP_VOICE] Error sending voice data: {ex.Message}");
                isConnectedToServer = false;
            }
        }

        private async Task SendPingToServer()
        {
            try
            {
                byte[] pingPacket = Encoding.UTF8.GetBytes("PING");
                await udpClient.SendAsync(pingPacket, pingPacket.Length, serverEndpoint);
                Console.WriteLine("[UDP_PING] Sent ping to server");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UDP_PING] Error: {ex.Message}");
            }
        }

        public async Task SendAuthenticationUdp(string playerId, string voiceId)
        {
            try
            {
                var authRequest = new UdpAuthRequest
                {
                    PlayerId = playerId,
                    VoiceId = voiceId,
                    Command = "AUTH"
                };

                string jsonData = JsonSerializer.Serialize(authRequest);
                byte[] authPacket = Encoding.UTF8.GetBytes("AUTH" + jsonData);

                await udpClient.SendAsync(authPacket, authPacket.Length, serverEndpoint);
                Console.WriteLine($"[UDP_AUTH] Sent authentication for player {playerId}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UDP_AUTH] Error: {ex.Message}");
            }
        }

        public async Task SendPrioritySettingUdp(string settingType, string value)
        {
            try
            {
                var priorityCommand = new UdpPriorityCommand
                {
                    PlayerId = serverId,
                    SettingType = settingType,
                    Value = value,
                    Command = "PRIORITY"
                };

                string jsonData = JsonSerializer.Serialize(priorityCommand);
                byte[] priorityPacket = Encoding.UTF8.GetBytes("PRIO" + jsonData);

                await udpClient.SendAsync(priorityPacket, priorityPacket.Length, serverEndpoint);
                Console.WriteLine($"[UDP_PRIORITY] Sent priority setting: {settingType}={value}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UDP_PRIORITY] Error: {ex.Message}");
            }
        }

        #endregion

        #region Microphone Management

        public List<MicrophoneInfo> GetAvailableMicrophones()
        {
            return availableMicrophones;
        }

        public void SetAudioTransmission(bool enabled)
        {
            allowAudioTransmission = enabled;
            Console.WriteLine($"[AUDIO] Audio transmission {(enabled ? "enabled" : "disabled")}");
        }

        public void RefreshMicrophones()
        {
            try
            {
                availableMicrophones.Clear();
                var devices = deviceEnumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
                var defaultDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

                foreach (var device in devices)
                {
                    var micInfo = new MicrophoneInfo
                    {
                        Id = device.ID,
                        Name = device.FriendlyName,
                        Description = device.DeviceFriendlyName,
                        IsDefault = device.ID == defaultDevice?.ID,
                        IsEnabled = device.State == DeviceState.Active
                    };
                    availableMicrophones.Add(micInfo);
                }

                actionScriptBridge.SendMicrophoneList(availableMicrophones);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error refreshing microphones: {ex.Message}");
            }
        }

        public bool SelectMicrophone(string microphoneId, bool stopCurrent = true)
        {
            try
            {
                Console.WriteLine($"[MIC] Selecting microphone: {microphoneId}");

                ForceCleanupCurrentMicrophone();

                var device = deviceEnumerator.GetDevice(microphoneId);
                if (device == null || device.State != DeviceState.Active)
                {
                    return false;
                }

                selectedDevice = device;
                SelectedMicrophoneId = microphoneId;

                actionScriptBridge.SendSelectedMicrophone(microphoneId);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MIC] Error selecting microphone: {ex.Message}");
                return false;
            }
        }

        public bool SelectDefaultMicrophone(bool stopCurrent = true)
        {
            var defaultMic = availableMicrophones.FirstOrDefault(m => m.IsDefault);
            return defaultMic != null && SelectMicrophone(defaultMic.Id, stopCurrent);
        }

        #endregion

        #region Audio Recording with Opus Encoding

        public void StartMicrophone()
        {
            Console.WriteLine("[MIC] Starting microphone...");
            if (selectedDevice == null)
            {
                if (!SelectDefaultMicrophone(false))
                {
                    Console.WriteLine("[MIC] No microphone available");
                    return;
                }
            }

            try
            {
                int deviceNumber = GetDeviceNumber(SelectedMicrophoneId);

                waveIn = new WaveInEvent
                {
                    DeviceNumber = deviceNumber,
                    WaveFormat = new WaveFormat(48000, 16, 1), // 48kHz for Opus
                    BufferMilliseconds = 20 // 20ms chunks for Opus
                };

                waveIn.DataAvailable += OnAudioDataAvailable;
                waveIn.RecordingStopped += OnRecordingStopped;
                waveIn.StartRecording();

                IsMicrophoneEnabled = true;
                audioUpdateCounter = 0;

                actionScriptBridge.SendMicrophoneStatus(true);
                Console.WriteLine("[MIC] Microphone started successfully");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MIC] Error starting microphone: {ex.Message}");
            }
        }

        public void StopMicrophone(bool sendStatus = true)
        {
            try
            {
                waveIn?.StopRecording();
                waveIn?.Dispose();
                waveIn = null;

                IsMicrophoneEnabled = false;
                currentLevel = 0f;
                smoothedLevel = 0f;
                peakLevel = 0f;

                if (sendStatus)
                {
                    actionScriptBridge.SendMicrophoneStatus(false);
                }
                
                Console.WriteLine("[MIC] Microphone stopped");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MIC] Error stopping microphone: {ex.Message}");
            }
        }

        private void OnAudioDataAvailable(object sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded < 100) return;

            audioUpdateCounter++;

            // Calculate audio level
            float maxSample = 0f;
            for (int i = 0; i < e.BytesRecorded; i += 2)
            {
                if (i + 1 < e.BytesRecorded)
                {
                    short sample = BitConverter.ToInt16(e.Buffer, i);
                    float abs = Math.Abs(sample / 32768f);
                    if (abs > maxSample) maxSample = abs;
                }
            }

            float level = maxSample * MicrophoneSensitivity;
            smoothedLevel = smoothedLevel * 0.7f + level * 0.3f;

            // Send audio if above noise gate and connected
            var now = DateTime.Now;
            if (isConnectedToServer && 
                isAuthenticated &&
                level > NoiseGate && 
                allowAudioTransmission && 
                (now - lastAudioSent).TotalMilliseconds >= AUDIO_SEND_INTERVAL_MS)
            {
                // Copy audio data
                byte[] audioData = new byte[e.BytesRecorded];
                Array.Copy(e.Buffer, audioData, e.BytesRecorded);

                // Encode to Opus
                byte[] opusData = opusProcessor.EncodeToOpus(audioData);
                
                // Queue for UDP sending
                outgoingOpusData.Enqueue(opusData);
                lastAudioSent = now;

                Console.WriteLine($"[OPUS] Encoded {audioData.Length} PCM → {opusData.Length} Opus bytes (level: {level:F3})");
            }

            // Update UI
            if (audioUpdateCounter >= 10) // Update UI less frequently
            {
                actionScriptBridge.SendAudioLevel(smoothedLevel);
                audioUpdateCounter = 0;
            }
        }

        private void ForceCleanupCurrentMicrophone()
        {
            try
            {
                if (waveIn != null)
                {
                    waveIn.DataAvailable -= OnAudioDataAvailable;
                    waveIn.RecordingStopped -= OnRecordingStopped;

                    try { waveIn.StopRecording(); } catch { }
                    try { waveIn.Dispose(); } catch { }
                    waveIn = null;
                }

                if (selectedDevice != null)
                {
                    try { selectedDevice.Dispose(); } catch { }
                    selectedDevice = null;
                }

                IsMicrophoneEnabled = false;
                currentLevel = 0f;
                smoothedLevel = 0f;
                peakLevel = 0f;

                // Clear audio queue
                while (outgoingOpusData.TryDequeue(out _)) { }

                Console.WriteLine("[MIC] Microphone cleanup completed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[MIC] Cleanup error: {ex.Message}");
            }
        }

        private void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            Console.WriteLine("[MIC] Recording stopped");
        }

        private int GetDeviceNumber(string deviceId)
        {
            for (int i = 0; i < WaveInEvent.DeviceCount; i++)
            {
                var capabilities = WaveInEvent.GetCapabilities(i);

                if (selectedDevice != null &&
                    capabilities.ProductName.Contains(selectedDevice.FriendlyName.Split(' ')[0]))
                {
                    return i;
                }
            }

            return 0;
        }

        #endregion

        #region UDP Server Connection

        public async Task<bool> ConnectToServer(string serverAddress, int port, string playerId, string voiceId)
        {
            Console.WriteLine($"[UDP_CONNECT] Connecting to {serverAddress}:{port}");
            try
            {
                lock (reconnectionLock)
                {
                    if (isConnectedToServer)
                    {
                        Console.WriteLine("[UDP_CONNECT] Already connected");
                        return true;
                    }
                }

                DisconnectFromServer();

                // Create UDP client
                udpClient = new UdpClient();
                serverEndpoint = new IPEndPoint(IPAddress.Parse(serverAddress), port);

                serverId = playerId;
                storedVoiceId = voiceId;
                storedServerAddress = serverAddress;
                storedPort = port;

                // Test connection with ping
                await SendPingToServer();
                
                // Send authentication
                await SendAuthenticationUdp(playerId, voiceId);

                // Start UDP sender worker
                udpSenderTask = Task.Run(UdpSenderWorker, udpCancellation.Token);

                isConnectedToServer = true;
                isAuthenticated = true; // Assume success for now
                lastConnectionCheck = DateTime.Now;

                ConnectionStatusChanged?.Invoke(this, "Connected (UDP)");
                Console.WriteLine($"[UDP_CONNECT] Connected successfully");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UDP_CONNECT] Error: {ex.Message}");
                ConnectionStatusChanged?.Invoke(this, $"Connection failed: {ex.Message}");
                return false;
            }
        }

        public void DisconnectFromServer()
        {
            try
            {
                isConnectedToServer = false;
                isAuthenticated = false;
                
                udpClient?.Close();
                udpClient?.Dispose();
                udpClient = null;

                ConnectionStatusChanged?.Invoke(this, "Disconnected");
                Console.WriteLine("[UDP_DISCONNECT] Disconnected from server");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[UDP_DISCONNECT] Error: {ex.Message}");
            }
        }

        public async Task SendServerMessage(string message)
        {
            // For UDP, we'll send priority settings instead
            var parts = message.Split(':');
            if (parts.Length >= 2)
            {
                await SendPrioritySettingUdp(parts[0], parts[1]);
            }
        }

        public void OnConnectionLost()
        {
            Console.WriteLine("[UDP] Connection lost event triggered");
            isConnectedToServer = false;
            isAuthenticated = false;
            
            while (outgoingOpusData.TryDequeue(out _)) { }
            
            if (!string.IsNullOrEmpty(storedServerAddress))
            {
                _ = Task.Run(async () => await ConnectToServer(storedServerAddress, storedPort, serverId, storedVoiceId));
            }
        }

        #endregion

        #region Public API Methods

        public void SetMicrophoneSensitivity(float sensitivity)
        {
            MicrophoneSensitivity = Math.Max(0.1f, Math.Min(5.0f, sensitivity));
        }

        public void SetNoiseGate(float threshold)
        {
            NoiseGate = Math.Max(0.0f, Math.Min(1.0f, threshold));
        }

        public AudioData GetCurrentAudioData()
        {
            return new AudioData
            {
                Level = smoothedLevel,
                Peak = peakLevel,
                IsActive = currentLevel > NoiseGate,
                Timestamp = DateTime.Now
            };
        }

        public void SetVoiceManager(VoiceManager vm)
        {
            voiceManager = vm;
            Console.WriteLine("[LINK] VoiceManager linked to ProximityChatManager");
        }

        public bool IsMicrophoneActive()
        {
            return IsMicrophoneEnabled && currentLevel > NoiseGate;
        }

        public bool IsConnected => isConnectedToServer;
        public bool UseUdpVoice => true; // Always UDP now

        #endregion

        #region IDisposable

        public void Dispose()
        {
            StopMicrophone();
            DisconnectFromServer();

            udpCancellation.Cancel();
            try
            {
                udpSenderTask?.Wait(1000);
            }
            catch { }

            deviceEnumerator?.Dispose();
            selectedDevice?.Dispose();
            actionScriptBridge?.Dispose();
            udpCancellation.Dispose();
        }

        #endregion
    }

    // ActionScript Bridge - unchanged
    public class ActionScriptBridge : IDisposable
    {
        private float lastSentLevel = 0f;
        private int levelUpdateCounter = 0;

        public ActionScriptBridge()
        {
            // Minimal initialization
        }

        public void SendMicrophoneList(List<MicrophoneInfo> microphones)
        {
            try
            {
                Console.WriteLine($"MIC_COUNT:{microphones.Count}");

                foreach (var mic in microphones)
                {
                    Console.WriteLine($"MIC_DEVICE:{mic.Id}|{mic.Name}|{mic.IsDefault}");
                }

                var defaultMic = microphones.FirstOrDefault(m => m.IsDefault);
                if (defaultMic != null)
                {
                    Console.WriteLine($"DEFAULT_MIC:{defaultMic.Id}");
                }
            }
            catch { }
        }

        public void SendSelectedMicrophone(string microphoneId)
        {
            try
            {
                Console.WriteLine($"SELECTED_MIC:{microphoneId ?? "none"}");
            }
            catch { }
        }

        public void SendAudioLevel(float level)
        {
            try
            {
                levelUpdateCounter++;

                if (levelUpdateCounter >= 1)
                {
                    Console.Out.WriteLine($"AUDIO_LEVEL:{level:F2}");
                    Console.Out.Flush();
                    levelUpdateCounter = 0;
                    lastSentLevel = level;
                }
            }
            catch { }
        }

        public void SendMicrophoneStatus(bool isEnabled)
        {
            try
            {
                Console.WriteLine($"MIC_STATUS:{isEnabled}");
            }
            catch { }
        }

        public void Dispose()
        {
            // Nothing to dispose
        }
    }

    // Main Program - updated for UDP
    class Program
    {
        private static CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();

        static async Task Main(string[] args)
        {
            var chatManager = new ProximityChatManager();
            var actionScriptBridge = new ActionScriptBridge();
            var voiceManager = new VoiceManager(actionScriptBridge);

            // Link the managers
            chatManager.SetVoiceManager(voiceManager);
            voiceManager.SetChatManagerReference(chatManager);

            // Start voice receiver (for incoming UDP voice)
            if (voiceManager.StartVoiceReceiver())
            {
                Console.WriteLine("🎉 UDP Voice System started successfully!");
                Console.WriteLine("Features: Opus compression, proximity chat, priority system");
            }
            else
            {
                Console.WriteLine("❌ ERROR: Voice system failed to start");
                return;
            }

            _ = Task.Run(async () => await ListenForCommands(chatManager, voiceManager, cancellationTokenSource.Token));

            Console.WriteLine("🚀 UDP + Opus proximity chat ready!");
            Console.WriteLine("Commands:");
            Console.WriteLine("  CONNECT_VOICE:serverIP:playerID:voiceID");
            Console.WriteLine("  START_MIC / STOP_MIC");
            Console.WriteLine("  SET_INCOMING_VOLUME:0.5");

            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                cancellationTokenSource.Cancel();
                _ = Task.Run(async () =>
                {
                    await Task.Delay(3000);
                    Environment.Exit(0);
                });
            };

            try
            {
                while (!cancellationTokenSource.Token.IsCancellationRequested)
                {
                    await Task.Delay(1000, cancellationTokenSource.Token);
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Shutting down...");
            }
            finally
            {
                chatManager.Dispose();
                voiceManager.Dispose();
            }
        }

        private static async Task ListenForCommands(ProximityChatManager chatManager, VoiceManager voiceManager,
            CancellationToken cancellationToken)
        {
            try
            {
                Console.WriteLine("Listening for commands...");

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        string command = Console.ReadLine();
                        if (string.IsNullOrEmpty(command)) continue;

                        var parts = command.Split(':');

                        switch (parts[0])
                        {
                            case "START_MIC":
                                chatManager.StartMicrophone();
                                break;

                            case "CONNECT_VOICE":
                                if (parts.Length >= 4)
                                {
                                    string serverIP = parts[1];
                                    string playerID = parts[2];
                                    string voiceID = parts[3];
                                    
                                    Console.WriteLine($"🔌 Connecting UDP voice for player {playerID}...");

                                    // Store connection details in VoiceManager
                                    voiceManager.StoreConnectionDetails(serverIP, playerID, voiceID);

                                    // Authenticate with voice server (UDP)
                                    await voiceManager.SendAuthenticationToServer(serverIP, 2051, playerID, voiceID);

                                    // Connect ProximityChatManager (UDP)
                                    bool connected = await chatManager.ConnectToServer(serverIP, 2051, playerID, voiceID);
                                    
                                    if (connected)
                                    {
                                        Console.WriteLine("✅ UDP voice connection established!");
                                    }
                                    else
                                    {
                                        Console.WriteLine("❌ UDP voice connection failed!");
                                    }
                                }
                                break;

                            case "STOP_MIC":
                                chatManager.StopMicrophone();
                                break;

                            case "ENABLE_AUDIO_TRANSMISSION":
                                chatManager.SetAudioTransmission(true);
                                break;

                            case "DISABLE_AUDIO_TRANSMISSION":
                                chatManager.SetAudioTransmission(false);
                                break;

                            case "SET_INCOMING_VOLUME":
                                if (parts.Length > 1)
                                {
                                    if (float.TryParse(parts[1], out float volume))
                                    {
                                        voiceManager.SetIncomingVolume(volume);
                                        Console.WriteLine($"🔊 Set incoming volume to {volume}");
                                    }
                                    else
                                    {
                                        Console.WriteLine($"❌ Invalid volume value: {parts[1]}");
                                    }
                                }
                                break;

                            case "SET_MIC_SENSITIVITY":
                                if (parts.Length > 1)
                                {
                                    if (float.TryParse(parts[1], out float sensitivity))
                                    {
                                        chatManager.SetMicrophoneSensitivity(sensitivity);
                                        Console.WriteLine($"🎤 Set microphone sensitivity to {sensitivity}");
                                    }
                                }
                                break;

                            case "SET_NOISE_GATE":
                                if (parts.Length > 1)
                                {
                                    if (float.TryParse(parts[1], out float gate))
                                    {
                                        chatManager.SetNoiseGate(gate);
                                        Console.WriteLine($"🔇 Set noise gate to {gate}");
                                    }
                                }
                                break;

                            case "PRIORITY_SETTING":
                                if (parts.Length >= 3)
                                {
                                    string settingType = parts[1];
                                    string value = parts[2];
                                    await chatManager.SendPrioritySettingUdp(settingType, value);
                                    Console.WriteLine($"⚡ Sent priority setting: {settingType}={value}");
                                }
                                break;

                            case "RECONNECT":
                                Console.WriteLine("🔄 Manual reconnection triggered");
                                chatManager.OnConnectionLost();
                                break;

                            case "STATUS":
                                Console.WriteLine("📊 UDP Voice System Status:");
                                Console.WriteLine($"   Connection: {(chatManager.IsConnected ? "Connected" : "Disconnected")}");
                                Console.WriteLine($"   Microphone: {(chatManager.IsMicrophoneEnabled ? "Active" : "Inactive")}");
                                Console.WriteLine($"   Protocol: UDP + Opus");
                                Console.WriteLine($"   Audio Level: {chatManager.GetCurrentAudioData().Level:F3}");
                                break;

                            case "AUDIO_PROBE":
                                var defaultDevice = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
                                Console.WriteLine($"AUDIO_INFO:Device={defaultDevice.FriendlyName}");
                                Console.WriteLine($"AUDIO_INFO:SampleRate={defaultDevice.AudioClient.MixFormat.SampleRate}");
                                Console.WriteLine($"AUDIO_INFO:Channels={defaultDevice.AudioClient.MixFormat.Channels}");
                                Console.WriteLine($"AUDIO_INFO:BitDepth={defaultDevice.AudioClient.MixFormat.BitsPerSample}");
                                break;

                            case "SELECT_MIC":
                                if (parts.Length > 1)
                                {
                                    bool wasRunning = chatManager.IsMicrophoneEnabled;
                                    bool success = chatManager.SelectMicrophone(parts[1]);
                                    Console.WriteLine($"SELECTED_MIC:{success}");

                                    if (success && wasRunning)
                                    {
                                        chatManager.StartMicrophone();
                                    }
                                }
                                break;

                            case "GET_MICS":
                                chatManager.RefreshMicrophones();
                                break;

                            case "TEST_OPUS":
                                Console.WriteLine("🧪 Testing Opus compression...");
                                var testAudio = new byte[1920]; // 20ms of 48kHz audio
                                new Random().NextBytes(testAudio);
                                var opusProcessor = new OpusAudioProcessor();
                                var compressed = opusProcessor.EncodeToOpus(testAudio);
                                var decompressed = opusProcessor.DecodeFromOpus(compressed);
                                Console.WriteLine($"   Original: {testAudio.Length} bytes");
                                Console.WriteLine($"   Compressed: {compressed.Length} bytes ({(float)compressed.Length/testAudio.Length*100:F1}%)");
                                Console.WriteLine($"   Decompressed: {decompressed.Length} bytes");
                                break;

                            case "HELP":
                                Console.WriteLine("🎮 Available Commands:");
                                Console.WriteLine("  CONNECT_VOICE:IP:playerID:voiceID - Connect to voice server");
                                Console.WriteLine("  START_MIC / STOP_MIC - Control microphone");
                                Console.WriteLine("  SET_INCOMING_VOLUME:0.5 - Set playback volume (0.0-1.0)");
                                Console.WriteLine("  SET_MIC_SENSITIVITY:1.0 - Set mic sensitivity");
                                Console.WriteLine("  SET_NOISE_GATE:0.01 - Set noise gate threshold");
                                Console.WriteLine("  PRIORITY_SETTING:type:value - Send priority command");
                                Console.WriteLine("  STATUS - Show system status");
                                Console.WriteLine("  TEST_OPUS - Test compression");
                                Console.WriteLine("  EXIT / QUIT - Shutdown");
                                break;

                            case "EXIT":
                            case "QUIT":
                                Console.WriteLine("👋 Shutting down UDP voice system...");
                                cancellationTokenSource.Cancel();
                                return;

                            default:
                                Console.WriteLine($"❓ Unknown command: {parts[0]} (type HELP for commands)");
                                break;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"❌ Command error: {ex.Message}");
                        Thread.Sleep(100);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Command listener stopped");
            }
        }
    }
}
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

    public class ChatMessage
    {
        public string PlayerId { get; set; }
        public string PlayerName { get; set; }
        public byte[] AudioData { get; set; }
        public float Volume { get; set; }
        public DateTime Timestamp { get; set; }
    }

    // ULTRA-OPTIMIZED: Minimal allocations proximity chat manager
    public class ProximityChatManager : IDisposable
    {
        #region Fields

        private WaveInEvent waveIn;
        private MMDeviceEnumerator deviceEnumerator;
        private MMDevice selectedDevice;
        private List<MicrophoneInfo> availableMicrophones;
        
        private VoiceManager voiceManager;
        private DateTime lastAudioSent = DateTime.MinValue;
        private const int AUDIO_SEND_INTERVAL_MS = 100;
        
// 1. ADD this field to your VoiceManager class (at the top with other fields):
        

        // Audio processing - REMOVED OBJECT POOLING (was causing overhead)
        private volatile float currentLevel;
        private volatile float peakLevel;
        private volatile float smoothedLevel;

        // MASSIVELY REDUCED update frequency
        private int audioUpdateCounter = 0;
        private const int AUDIO_UPDATE_SKIP = 100; // Only update every 100th frame (~1Hz instead of 20Hz)

        // Communication
        private TcpClient serverConnection;
        private NetworkStream serverStream;
        private volatile bool isConnectedToServer;
        private string serverId;

        // REDUCED: Smaller queues, less frequent processing
        private readonly ConcurrentQueue<byte[]> outgoingAudioData = new ConcurrentQueue<byte[]>();
        private Task networkWorkerTask;
        private readonly CancellationTokenSource networkCancellation = new CancellationTokenSource();

        // ActionScript communication - MINIMAL
        private ActionScriptBridge actionScriptBridge;

        // Settings
        public bool IsMicrophoneEnabled { get; private set; }
        public string SelectedMicrophoneId { get; private set; }
        public float MicrophoneSensitivity { get; set; } = 1.0f;
        public float NoiseGate { get; set; } = 0.02f; // Higher default to reduce processing

        private float lastSentLevel = 0f;

        public bool allowAudioTransmission = true;

        public float MicrophoneGain { get; set; } = 3f;
        public float MakeupGain { get; set; } = 1.0f; // NO boost at all
        public float CompressorThreshold { get; set; } = 0.9f; // Very high threshold  
        public float CompressorRatio { get; set; } = 1.5f; // Very gentle compression

        public float LimiterThreshold { get; set; } = 0.8f;

        // REMOVED: Most events to reduce overhead
        public event EventHandler<string> ConnectionStatusChanged;
        private DateTime lastConnectionCheck = DateTime.Now;
        private readonly object reconnectionLock = new object();
        private bool isReconnecting = false;
        private string storedServerAddress;
        private int storedPort;
        private string storedVoiceId;
        #endregion

        #region Constructor and Initialization

        public ProximityChatManager()
        {
            deviceEnumerator = new MMDeviceEnumerator();
            availableMicrophones = new List<MicrophoneInfo>();
            actionScriptBridge = new ActionScriptBridge();

            currentLevel = 0f;
            peakLevel = 0f;
            smoothedLevel = 0f;

            // REDUCED: Much less frequent network processing
            networkWorkerTask = Task.Run(NetworkWorker, networkCancellation.Token);

            RefreshMicrophones();
        }

        #endregion

        #region ULTRA-OPTIMIZED: Background Network Worker

        // REPLACE this in your NetworkWorker() method (around line 119):

    
       
       private async Task NetworkWorker()
{
    while (!networkCancellation.Token.IsCancellationRequested)
    {
        try
        {
            // CHECK CONNECTION HEALTH periodically
            var now = DateTime.Now;
            if ((now - lastConnectionCheck).TotalSeconds >= 30) // Check every 30 seconds
            {
                if (!IsConnectionHealthy())
                {
                    Console.WriteLine("Connection unhealthy - attempting reconnection");
                    await AttemptReconnection(serverId, storedVoiceId);
                }
                lastConnectionCheck = now;
            }

            // SEND ONLY ONE AUDIO CHUNK AT A TIME
            if (outgoingAudioData.TryDequeue(out var audioData) && isConnectedToServer)
            {
                var chatMessage = new ChatMessage
                {
                    PlayerId = serverId,
                    PlayerName = "LocalPlayer",
                    AudioData = audioData,
                    Volume = smoothedLevel,
                    Timestamp = DateTime.Now
                };

                var json = JsonSerializer.Serialize(chatMessage);
                var message = $"VOICE_DATA:{json}";
                var messageBytes = Encoding.UTF8.GetBytes(message);

                var lengthBytes = BitConverter.GetBytes(messageBytes.Length);
        
                await serverStream.WriteAsync(lengthBytes, 0, 4);
                await serverStream.WriteAsync(messageBytes, 0, messageBytes.Length);
        
                Console.Error.WriteLine($"DEBUG: Sent single audio chunk, {audioData.Length} bytes");
            }

            await Task.Delay(10, networkCancellation.Token);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR in NetworkWorker: {ex.Message}");
            
            // TRIGGER RECONNECTION on network errors
            isConnectedToServer = false;
            if (!string.IsNullOrEmpty(storedServerAddress))
            {
                Console.WriteLine("Network error detected - triggering reconnection");
                await AttemptReconnection(serverId, storedVoiceId);
            }
            
            await Task.Delay(100, networkCancellation.Token);
        }
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
            Console.WriteLine($"DEBUG: Audio transmission {(enabled ? "enabled" : "disabled")}");
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

                // Send minimal data to prevent ActionScript null reference
                actionScriptBridge.SendMicrophoneList(availableMicrophones);
            }
            catch (Exception ex)
            {
                // Minimal error handling
            }
        }

        public bool SelectMicrophone(string microphoneId, bool stopCurrent = true)
        {
            try
            {
                Console.WriteLine($"DEBUG: SelectMicrophone called with ID: {microphoneId}");

                // ADD THIS ONE LINE:
                ForceCleanupCurrentMicrophone();

                // Rest of your existing code stays the same:
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
                Console.WriteLine($"DEBUG: Error in SelectMicrophone: {ex.Message}");
                return false;
            }
        }

        public bool SelectDefaultMicrophone(bool stopCurrent = true)
        {
            var defaultMic = availableMicrophones.FirstOrDefault(m => m.IsDefault);
            return defaultMic != null && SelectMicrophone(defaultMic.Id, stopCurrent);
        }

        #endregion

        #region ULTRA-OPTIMIZED Audio Recording

        public void StartMicrophone()
        
        {
            Console.Error.WriteLine($"DEBUG: Starting microphone...");
            if (selectedDevice == null)
            {
                if (!SelectDefaultMicrophone(false))
                {

                    return;
                }
            }

            try
            {
                int deviceNumber = GetDeviceNumber(SelectedMicrophoneId);



                waveIn = new WaveInEvent
                {
                    DeviceNumber = deviceNumber,
                    WaveFormat = new WaveFormat(44100, 16, 1), // 44.1kHz instead of 16kHz - MUCH better quality
                    BufferMilliseconds = 150
                };


                waveIn.DataAvailable += OnAudioDataAvailable;
                waveIn.RecordingStopped += OnRecordingStopped;


                waveIn.StartRecording();


                IsMicrophoneEnabled = true;
                audioUpdateCounter = 0;

                actionScriptBridge.SendMicrophoneStatus(true);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR in StartMicrophone: {ex.Message}");
                File.AppendAllText("error.log", $"{DateTime.Now}: StartMicrophone - {ex}\n");


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

                // Only send status update if requested
                if (sendStatus)
                {
                    actionScriptBridge.SendMicrophoneStatus(false);
                }
            }
            catch (Exception ex)
            {
                // REMOVED: Console output
            }
        }

        private float ApplyCompressor(float input)
        {
            float absInput = Math.Abs(input);

            if (absInput <= CompressorThreshold)
            {
                // Below threshold: no compression
                return input;
            }
            else
            {
                // Above threshold: apply compression
                float excessLevel = absInput - CompressorThreshold;
                float compressedExcess = excessLevel / CompressorRatio;
                float compressedLevel = CompressorThreshold + compressedExcess;

                // Maintain original sign
                return input >= 0 ? compressedLevel : -compressedLevel;
            }
        }

        private float ApplyLimiter(float input)
        {
            // Hard limit - never allow audio above the limiter threshold
            if (input > LimiterThreshold)
                return LimiterThreshold;
            else if (input < -LimiterThreshold)
                return -LimiterThreshold;
            else
                return input;
        }

        // ULTRA-OPTIMIZED: Absolute minimal processing
        private void OnAudioDataAvailable(object sender, WaveInEventArgs e)
        {
            if (e.BytesRecorded < 100) return;

            audioUpdateCounter++;

            // Calculate audio level (keep existing code)
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

            // THROTTLE AUDIO SENDING - only send every 100ms
            var now = DateTime.Now;
            if (isConnectedToServer && 
                level > 0.005f && 
                allowAudioTransmission && 
                (now - lastAudioSent).TotalMilliseconds >= AUDIO_SEND_INTERVAL_MS)
            {
                byte[] audioData = new byte[e.BytesRecorded];
                Array.Copy(e.Buffer, audioData, e.BytesRecorded);
                outgoingAudioData.Enqueue(audioData);
        
                lastAudioSent = now; // Update last send time
        
                Console.Error.WriteLine($"DEBUG: Queued {audioData.Length} bytes for sending (level: {level:F3})");
            }
            else if (level > 0.005f && allowAudioTransmission)
            {
                Console.Error.WriteLine($"DEBUG: Audio throttled - too soon since last send");
            }

            // Update UI (keep existing code)
            if (audioUpdateCounter >= 1)
            {
                actionScriptBridge.SendAudioLevel(smoothedLevel);
                audioUpdateCounter = 0;
            }
        }



// 1. ADD this new cleanup function to your ProximityChatManager class:

        private void ForceCleanupCurrentMicrophone()
        {
            try
            {
                Console.Error.WriteLine("DEBUG: Cleaning up current microphone...");

                if (waveIn != null)
                {
                    // Remove event handlers first
                    waveIn.DataAvailable -= OnAudioDataAvailable;
                    waveIn.RecordingStopped -= OnRecordingStopped;

                    // Stop and dispose
                    try
                    {
                        waveIn.StopRecording();
                    }
                    catch
                    {
                    }

                    try
                    {
                        waveIn.Dispose();
                    }
                    catch
                    {
                    }

                    waveIn = null;
                }

                // Clear device reference
                if (selectedDevice != null)
                {
                    try
                    {
                        selectedDevice.Dispose();
                    }
                    catch
                    {
                    }

                    selectedDevice = null;
                }

                // Reset state
                IsMicrophoneEnabled = false;
                currentLevel = 0f;
                smoothedLevel = 0f;
                peakLevel = 0f;

                // Clear audio queue
                while (outgoingAudioData.TryDequeue(out _))
                {
                }

                Console.Error.WriteLine("DEBUG: Microphone cleanup completed");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"DEBUG: Error in cleanup: {ex.Message}");
            }
        }

        private void OnRecordingStopped(object sender, StoppedEventArgs e)
        {
            // REMOVED: Console output
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

        #region OPTIMIZED Server Communication
       
        public async Task<bool> ConnectToServer(string serverAddress, int port, string playerId, string voiceId)
{
    Console.Error.WriteLine($"DEBUG: Attempting to connect to {serverAddress}:{port}");
    try
    {
        lock (reconnectionLock)
        {
            if (isConnectedToServer && serverConnection?.Connected == true)
            {
                Console.Error.WriteLine("Already connected to server");
                return true;
            }
        }

        DisconnectFromServer();

        serverConnection = new TcpClient();
        // ADD: Set keep-alive to detect dead connections
        serverConnection.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        
        Console.Error.WriteLine($"DEBUG: Created TcpClient, connecting...");
        await serverConnection.ConnectAsync(serverAddress, port);
        Console.Error.WriteLine($"DEBUG: Connected! Getting stream...");
        serverStream = serverConnection.GetStream();

        isConnectedToServer = true;
        serverId = playerId;
        lastConnectionCheck = DateTime.Now; // RESET connection check timer
        storedServerAddress = serverAddress;
        storedPort = port;
        storedVoiceId = voiceId;

        // SIMPLE: Just authenticate this sender
        var identMessage = $"VOICE_CONNECT:{playerId}:{voiceId}";
        var messageBytes = Encoding.UTF8.GetBytes(identMessage);

        var lengthBytes = new byte[4];
        lengthBytes[0] = (byte)(messageBytes.Length & 0xFF);
        lengthBytes[1] = (byte)((messageBytes.Length >> 8) & 0xFF);
        lengthBytes[2] = (byte)((messageBytes.Length >> 16) & 0xFF);
        lengthBytes[3] = (byte)((messageBytes.Length >> 24) & 0xFF);

        await serverStream.WriteAsync(lengthBytes, 0, 4);
        await serverStream.WriteAsync(messageBytes, 0, messageBytes.Length);

        Console.WriteLine($"DEBUG: Sent VOICE_CONNECT for player {playerId}");

        _ = Task.Run(ListenForServerMessages);
        ConnectionStatusChanged?.Invoke(this, "Connected");
        return true;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR in ConnectToServer: {ex.Message}");
        File.AppendAllText("error.log", $"{DateTime.Now}: ConnectToServer - {ex}\n");
        ConnectionStatusChanged?.Invoke(this, $"Connection failed: {ex.Message}");
        return false;
    }
}
        private bool IsConnectionHealthy()
        {
            try
            {
                if (serverConnection == null || !serverConnection.Connected)
                    return false;

                // CHECK: Socket is actually connected (not just cached state)
                Socket socket = serverConnection.Client;
                return !(socket.Poll(1000, SelectMode.SelectRead) && socket.Available == 0);
            }
            catch
            {
                return false;
            }
        }

        private async Task AttemptReconnection(string playerId, string voiceId)
        {
            lock (reconnectionLock)
            {
                if (isReconnecting) return;
                isReconnecting = true;
            }

            try
            {
                Console.WriteLine("DEBUG: Attempting auto-reconnection...");

                if (!string.IsNullOrEmpty(storedServerAddress))
                {
                    await ConnectToServer(storedServerAddress, storedPort, playerId, voiceId);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DEBUG: Auto-reconnection failed: {ex.Message}");
            }
            finally
            {
                lock (reconnectionLock)
                {
                    isReconnecting = false;
                }
            }
        }

        public void DisconnectFromServer()
        {
            try
            {
                isConnectedToServer = false;
                serverStream?.Close();
                serverConnection?.Close();
                ConnectionStatusChanged?.Invoke(this, "Disconnected");
            }
            catch (Exception ex)
            {
                // REMOVED: Console output
            }
        }

        // OPTIMIZED: Batch sending to reduce network calls
        // REPLACE your SendBatchedAudioToServer method with this:
        private async Task SendBatchedAudioToServer(List<byte[]> audioBatch)
        {
            try
            {
                // Send ALL audio chunks as raw PCM (no MP3 conversion)
                foreach (var audioChunk in audioBatch)
                {
                    var chatMessage = new ChatMessage
                    {
                        PlayerId = serverId,
                        PlayerName = "LocalPlayer",
                        AudioData = audioChunk, // Send raw PCM data directly
                        Volume = smoothedLevel,
                        Timestamp = DateTime.Now
                    };

                    var json = JsonSerializer.Serialize(chatMessage);
                    var message = $"VOICE_DATA:{json}";
                    var messageBytes = Encoding.UTF8.GetBytes(message);

                    // Send length + data
                    var lengthBytes = new byte[4];
                    lengthBytes[0] = (byte)(messageBytes.Length & 0xFF);
                    lengthBytes[1] = (byte)((messageBytes.Length >> 8) & 0xFF);
                    lengthBytes[2] = (byte)((messageBytes.Length >> 16) & 0xFF);
                    lengthBytes[3] = (byte)((messageBytes.Length >> 24) & 0xFF);

                    await serverStream.WriteAsync(lengthBytes, 0, 4);
                    await serverStream.WriteAsync(messageBytes, 0, messageBytes.Length);
                }

                Console.Error.WriteLine($"DEBUG: Sent {audioBatch.Count} raw PCM audio chunks to server");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERROR in SendBatchedAudioToServer: {ex.Message}");
                File.AppendAllText("error.log", $"{DateTime.Now}: SendBatchedAudioToServer - {ex}\n");
                isConnectedToServer = false;
            }
        }

    private async Task ListenForServerMessages()
{
    while (isConnectedToServer && serverConnection?.Connected == true)
    {
        try
        {
            // Read 4-byte length header first
            var lengthBuffer = new byte[4];
            int lengthBytesRead = 0;
            
            while (lengthBytesRead < 4)
            {
                int bytesRead = await serverStream.ReadAsync(lengthBuffer, lengthBytesRead, 4 - lengthBytesRead);
                if (bytesRead == 0) 
                {
                    Console.WriteLine("Server disconnected - no more data");
                    isConnectedToServer = false;
                    
                    // TRIGGER RECONNECTION
                    await AttemptReconnection(serverId, storedVoiceId);
                    break;
                }
                lengthBytesRead += bytesRead;
            }
            
            if (lengthBytesRead < 4) break;
            
            // Parse message length
            int messageLength = BitConverter.ToInt32(lengthBuffer, 0);
            
            if (messageLength > 200000 || messageLength < 0)
            {
                Console.WriteLine($"Invalid message length: {messageLength}");
                break;
            }
            
            // Read the exact message length
            var messageBuffer = new byte[messageLength];
            int messageBytesRead = 0;
            
            while (messageBytesRead < messageLength)
            {
                int bytesRead = await serverStream.ReadAsync(messageBuffer, messageBytesRead, messageLength - messageBytesRead);
                if (bytesRead == 0) 
                {
                    Console.WriteLine("Connection lost while reading message");
                    isConnectedToServer = false;
                    await AttemptReconnection(serverId, storedVoiceId);
                    break;
                }
                messageBytesRead += bytesRead;
            }
            
            if (messageBytesRead < messageLength) break;
            
            string message = Encoding.UTF8.GetString(messageBuffer, 0, messageLength);
            
            // Route voice messages to VoiceManager
            if (message.Contains("\"Type\":\"VOICE_AUDIO\""))
            {
                Console.WriteLine($"Routing complete TCP message ({messageLength} bytes) to VoiceManager");
                await voiceManager.ProcessVoiceTcpMessage(message);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in ListenForServerMessages: {ex.Message}");
            isConnectedToServer = false;
            
            // TRIGGER RECONNECTION
            await AttemptReconnection(serverId, storedVoiceId);
            break;
        }
    }
}
public void OnConnectionLost()
{
    Console.WriteLine("Connection lost event triggered");
    isConnectedToServer = false;
    
    // Clear any pending audio to prevent buildup
    while (outgoingAudioData.TryDequeue(out _)) { }
    
    // Trigger reconnection attempt
    if (!string.IsNullOrEmpty(storedServerAddress))
    {
        _ = Task.Run(async () => await AttemptReconnection(serverId, storedVoiceId));
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
            Console.WriteLine("VoiceManager linked to ProximityChatManager");
        }
        public bool IsMicrophoneActive()
        {
            return IsMicrophoneEnabled && currentLevel > NoiseGate;
        }
        public async Task SendPrioritySettingToServer(string settingType, string value)
        {
            try
            {
                if (!isConnectedToServer || serverStream == null)
                {
                    Console.WriteLine($"Cannot send priority setting - not connected to server");
                    return;
                }

                var message = $"PRIORITY_SETTING:{settingType}:{value}";
                var messageBytes = Encoding.UTF8.GetBytes(message);

                // Send length header
                var lengthBytes = new byte[4];
                lengthBytes[0] = (byte)(messageBytes.Length & 0xFF);
                lengthBytes[1] = (byte)((messageBytes.Length >> 8) & 0xFF);
                lengthBytes[2] = (byte)((messageBytes.Length >> 16) & 0xFF);
                lengthBytes[3] = (byte)((messageBytes.Length >> 24) & 0xFF);

                await serverStream.WriteAsync(lengthBytes, 0, 4);
                await serverStream.WriteAsync(messageBytes, 0, messageBytes.Length);

                Console.WriteLine($"Sent priority setting: {settingType} = {value}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending priority setting: {ex.Message}");
            }
        }
        #endregion

        #region IDisposable

        public void Dispose()
        {
            StopMicrophone();
            DisconnectFromServer();

            networkCancellation.Cancel();
            try
            {
                networkWorkerTask?.Wait(1000);
            }
            catch
            {
            }

            deviceEnumerator?.Dispose();
            selectedDevice?.Dispose();
            actionScriptBridge?.Dispose();
            networkCancellation.Dispose();
        }

        #endregion
    }

    // MINIMAL ActionScript Bridge - Only Essential Communication
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

                // Send each microphone with details
                foreach (var mic in microphones)
                {
                    Console.WriteLine($"MIC_DEVICE:{mic.Id}|{mic.Name}|{mic.IsDefault}");
                }

                // Send default mic
                var defaultMic = microphones.FirstOrDefault(m => m.IsDefault);
                if (defaultMic != null)
                {
                    Console.WriteLine($"DEFAULT_MIC:{defaultMic.Id}");
                }
            }
            catch
            {
            }
        }

        public void SendSelectedMicrophone(string microphoneId)
        {
            try
            {
                Console.WriteLine($"SELECTED_MIC:{microphoneId ?? "none"}");
            }
            catch
            {
            }
        }

        public void SendAudioLevel(float level)
        {
            try
            {
                //Console.WriteLine($"DEBUG_SEND_AUDIO_LEVEL: Called with level {level}, counter {levelUpdateCounter}");
                levelUpdateCounter++;

                // SEND EVERY TIME for testing
                if (levelUpdateCounter >= 1) // Always send
                {
                    Console.Out.WriteLine($"AUDIO_LEVEL:{level:F2}");
                    Console.Out.Flush();
                    levelUpdateCounter = 0;
                    lastSentLevel = level;
                }
            }
            catch
            {
            }
        }

        public void SendMicrophoneStatus(bool isEnabled)
        {
            try
            {
                Console.WriteLine($"MIC_STATUS:{isEnabled}");
            }
            catch
            {
            }
        }

        public void Dispose()
        {
            // Nothing to dispose
        }
    }

    // Minimal program
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
            voiceManager.SetChatManagerReference(chatManager); // NEW LINE
    
            // Start voice receiver
            if (voiceManager.StartVoiceReceiver())
            {
                Console.WriteLine("VoiceManager started successfully on port 2051");
            }
            else
            {
                Console.WriteLine("ERROR: VoiceManager FAILED to start on port 2051");
                return;
            }
            
            
            

            // ADD: Start voice receiver immediately
            if (voiceManager.StartVoiceReceiver())
            {
                Console.WriteLine("VoiceManager started successfully on port 2051");
            }
            else
            {
                Console.WriteLine("ERROR: VoiceManager FAILED to start on port 2051");
                return; // Exit if can't start voice receiver
            }

            _ = Task.Run(async () => await ListenForCommands(chatManager, voiceManager, cancellationTokenSource.Token));

            Console.WriteLine("Ultra-optimized proximity chat started. Type commands...");

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
                voiceManager.Dispose(); // ADD: Dispose VoiceManager
            }
        }

        private static async Task ListenForCommands(ProximityChatManager chatManager, VoiceManager voiceManager,
            CancellationToken cancellationToken)
        {
            try
            {
                Console.WriteLine("Listening for ActionScript commands...");

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
                                    Console.WriteLine($"DEBUG: Connecting voice for player {playerID}");

                                    // Store connection details in VoiceManager
                                    voiceManager.StoreConnectionDetails(serverIP, playerID, voiceID); // NEW LINE

                                    // Connect ProximityChatManager (for sending voice)
                                    _ = chatManager.ConnectToServer(serverIP, 2051, playerID, voiceID);
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
                                        Console.WriteLine($"Command received: Set incoming volume to {volume}");
                                    }
                                    else
                                    {
                                        Console.WriteLine($"Invalid volume value: {parts[1]}");
                                    }
                                }
                                break;
                            case "RECONNECT":
                                Console.WriteLine("Manual reconnection triggered");
                                chatManager.OnConnectionLost();
                                break;
                            case "AUDIO_PROBE":
                                // Get default audio device info
                                var defaultDevice = new MMDeviceEnumerator().GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
                                Console.WriteLine($"AUDIO_INFO:Device={defaultDevice.FriendlyName}");
                                Console.WriteLine($"AUDIO_INFO:SampleRate={defaultDevice.AudioClient.MixFormat.SampleRate}");
                                Console.WriteLine($"AUDIO_INFO:Channels={defaultDevice.AudioClient.MixFormat.Channels}");
                                Console.WriteLine($"AUDIO_INFO:BitDepth={defaultDevice.AudioClient.MixFormat.BitsPerSample}");
                                break;
                            // Add these cases to your existing switch statement in ListenForCommands

                            case "SET_PRIORITY_ENABLED":
                                if (parts.Length > 1)
                                {
                                    if (bool.TryParse(parts[1], out bool enabled))
                                    {
                                        await chatManager.SendPrioritySettingToServer("ENABLED", enabled.ToString());
                                        Console.WriteLine($"Priority system set to: {enabled}");
                                    }
                                }
                                break;

                            case "SET_PRIORITY_THRESHOLD":
                                if (parts.Length > 1)
                                {
                                    if (int.TryParse(parts[1], out int threshold))
                                    {
                                        await chatManager.SendPrioritySettingToServer("THRESHOLD", threshold.ToString());
                                        Console.WriteLine($"Priority threshold set to: {threshold}");
                                    }
                                }
                                break;

                            case "SET_NON_PRIORITY_VOLUME":
                                if (parts.Length > 1)
                                {
                                    if (float.TryParse(parts[1], out float volume))
                                    {
                                        await chatManager.SendPrioritySettingToServer("NON_PRIORITY_VOLUME", volume.ToString());
                                        Console.WriteLine($"Non-priority volume set to: {volume}");
                                    }
                                }
                                break;

                            case "ADD_MANUAL_PRIORITY":
                                if (parts.Length > 1)
                                {
                                    if (int.TryParse(parts[1], out int accountId))
                                    {
                                        await chatManager.SendPrioritySettingToServer("ADD_MANUAL", accountId.ToString());
                                        Console.WriteLine($"Added manual priority for account: {accountId}");
                                    }
                                }
                                break;

                            case "REMOVE_MANUAL_PRIORITY":
                                if (parts.Length > 1)
                                {
                                    if (int.TryParse(parts[1], out int accountId))
                                    {
                                        await chatManager.SendPrioritySettingToServer("REMOVE_MANUAL", accountId.ToString());
                                        Console.WriteLine($"Removed manual priority for account: {accountId}");
                                    }
                                }
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

                            case "EXIT":
                            case "QUIT":
                                cancellationTokenSource.Cancel();
                                Environment.Exit(0);
                                return;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Command error: {ex.Message}");
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
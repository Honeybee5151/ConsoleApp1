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

        // REMOVED: Most events to reduce overhead
        public event EventHandler<string> ConnectionStatusChanged;

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

        private async Task NetworkWorker()
        {
            var messageBuffer = new List<byte[]>(10); // Batch process audio

            while (!networkCancellation.Token.IsCancellationRequested)
            {
                try
                {
                    // Batch process audio data (much more efficient)
                    messageBuffer.Clear();
                    int count = 0;
                    while (outgoingAudioData.TryDequeue(out var audioData) && count < 5)
                    {
                        messageBuffer.Add(audioData);
                        count++;
                    }

                    // Send batched audio if any
                    if (messageBuffer.Count > 0 && isConnectedToServer)
                    {
                        await SendBatchedAudioToServer(messageBuffer);
                    }

                    // MUCH longer delay - only process network every 50ms
                    await Task.Delay(50, networkCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    // REMOVED: Console output during normal operation
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
                // Only stop if there's actually something running AND we want to stop it
                
                if (stopCurrent && waveIn != null && IsMicrophoneEnabled)
                {
                    StopMicrophone(sendStatus: false); // stop mic but keep button state
                }
                var device = deviceEnumerator.GetDevice(microphoneId);
                if (device == null || device.State != DeviceState.Active)
                    return false;

                selectedDevice = device;
                SelectedMicrophoneId = microphoneId;

                // Send selection to prevent ActionScript null reference
                actionScriptBridge.SendSelectedMicrophone(microphoneId);

                return true;
            }
            catch (Exception ex)
            {
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
                    WaveFormat = new WaveFormat(8000, 16, 1), // Change from 16000 to 8000
                    BufferMilliseconds = 100
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

        // ULTRA-OPTIMIZED: Absolute minimal processing
        private void OnAudioDataAvailable(object sender, WaveInEventArgs e)
        {
           // Console.WriteLine($"DEBUG_AUDIO_CALLBACK: BytesRecorded={e.BytesRecorded}, counter={audioUpdateCounter}");
            if (!IsMicrophoneEnabled || waveIn == null)
            {
                //Console.WriteLine("DEBUG_AUDIO_CALLBACK: Microphone disabled, ignoring callback");
                return;
            }

            if (e.BytesRecorded < 100) return;

            audioUpdateCounter++;

            // SIMPLIFIED: Process EVERY callback for testing
            float maxSample = 0f;
            for (int i = 0; i < e.BytesRecorded; i += 8)
            {
                if (i + 1 < e.BytesRecorded)
                {
                    short sample = BitConverter.ToInt16(e.Buffer, i);
                    float abs = Math.Abs(sample / 32768f);
                    if (abs > maxSample) maxSample = abs;
                }
            }

            float level = maxSample * MicrophoneSensitivity;
            smoothedLevel = smoothedLevel * 0.8f + level * 0.2f;

            // ADD THIS: Queue audio data for server transmission
            if (isConnectedToServer && level > NoiseGate)
            {
                byte[] audioData = new byte[e.BytesRecorded];
                Array.Copy(e.Buffer, audioData, e.BytesRecorded);
                outgoingAudioData.Enqueue(audioData);
               //Console.WriteLine($"DEBUG: Queued audio data, queue size: {outgoingAudioData.Count}");
            }

            // ALWAYS send audio level for testing - every 10 callbacks
            if (audioUpdateCounter >= 1)
            {
                actionScriptBridge.SendAudioLevel(smoothedLevel);
                audioUpdateCounter = 0; // Reset counter
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
            try
            {
                // Prevent duplicate connections
                if (isConnectedToServer && serverConnection?.Connected == true)
                {
                    Console.WriteLine("Already connected to server");
                    return true;
                }

                DisconnectFromServer();

                serverConnection = new TcpClient();
                await serverConnection.ConnectAsync(serverAddress, 2051);
                serverStream = serverConnection.GetStream();

                isConnectedToServer = true;
                serverId = playerId;

                // Send identification with VoiceID using length prefix
                var identMessage = $"VOICE_CONNECT:{playerId}:{voiceId}";
                var messageBytes = Encoding.UTF8.GetBytes(identMessage);
        
                // Send length header first
                var lengthBytes = new byte[4];
                lengthBytes[0] = (byte)(messageBytes.Length & 0xFF);
                lengthBytes[1] = (byte)((messageBytes.Length >> 8) & 0xFF);
                lengthBytes[2] = (byte)((messageBytes.Length >> 16) & 0xFF);
                lengthBytes[3] = (byte)((messageBytes.Length >> 24) & 0xFF);
        
                await serverStream.WriteAsync(lengthBytes, 0, 4);
                await serverStream.WriteAsync(messageBytes, 0, messageBytes.Length);

                Console.WriteLine($"DEBUG: Sent VOICE_CONNECT, {messageBytes.Length} bytes");

                _ = Task.Run(ListenForServerMessages);

                ConnectionStatusChanged?.Invoke(this, "Connected");
                return true;
            }
            catch (Exception ex)
            {
                ConnectionStatusChanged?.Invoke(this, $"Connection failed: {ex.Message}");
                return false;
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
        private async Task SendBatchedAudioToServer(List<byte[]> audioBatch)
        {
            try
            {
                var latestAudio = audioBatch[audioBatch.Count - 1];
                // No compression needed - just use the audio as-is
        
                var chatMessage = new ChatMessage
                {
                    PlayerId = serverId,
                    PlayerName = "LocalPlayer",
                    AudioData = latestAudio, // Use original audio
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

                Console.WriteLine($"DEBUG: Sent {messageBytes.Length} bytes to server");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending audio: {ex.Message}");
                isConnectedToServer = false;
            }
        }

        private async Task ListenForServerMessages()
        {
            byte[] buffer = new byte[8192];

            while (isConnectedToServer && serverConnection?.Connected == true)
            {
                try
                {
                    int bytesRead = await serverStream.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead > 0)
                    {
                        string message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                        // REMOVED: Message processing to reduce overhead
                    }
                }
                catch (Exception ex)
                {
                    break;
                }
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

        public bool IsMicrophoneActive()
        {
            return IsMicrophoneEnabled && currentLevel > NoiseGate;
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
            catch { }
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
            catch { }
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

            // REMOVED: Most event handlers to reduce overhead
            _ = Task.Run(() => ListenForCommands(chatManager, cancellationTokenSource.Token));

            Console.WriteLine("Ultra-optimized proximity chat started. Type commands...");

            Console.CancelKeyPress += (sender, e) =>
            {
                e.Cancel = true;
                cancellationTokenSource.Cancel();
            };

            try
            {
                while (!cancellationTokenSource.Token.IsCancellationRequested)
                {
                    await Task.Delay(1000, cancellationTokenSource.Token); // Much longer delay
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Shutting down...");
            }
            finally
            {
                chatManager.Dispose();
            }
        }

        private static void ListenForCommands(ProximityChatManager chatManager, CancellationToken cancellationToken)
        {
            try
            {
                // Add this line to see if we're receiving anything
                Console.WriteLine("Listening for ActionScript commands...");

                while (!cancellationToken.IsCancellationRequested)
                {
                    try
                    {
                        // This reads from stdin (what ActionScript sends)
                        string command = Console.ReadLine();

                        // Add debug output to see what we receive
                        

                        if (string.IsNullOrEmpty(command)) continue;

                        var parts = command.Split(':');
                        

                        switch (parts[0])
                        {
                            case "START_MIC":
                                
                                chatManager.StartMicrophone();
                                
                                break;
                            case "CONNECT_VOICE": // UPDATE THIS CASE
                                if (parts.Length >= 4) // Now expects: CONNECT_VOICE:serverIP:playerID:voiceID
                                {
                                    string serverIP = parts[1];
                                    string playerID = parts[2];
                                    string voiceID = parts[3];
                                    _ = chatManager.ConnectToServer(serverIP, 0, playerID, voiceID);
                                }
                                break;
                            case "STOP_MIC":
                                
                                chatManager.StopMicrophone();
                               
                                break;
                            case "SELECT_MIC":
                                if (parts.Length > 1)
                                {
                                    // Store the current state BEFORE selecting
                                    bool wasRunning = chatManager.IsMicrophoneEnabled;
        
                                    bool success = chatManager.SelectMicrophone(parts[1]);
                                    Console.WriteLine($"SELECTED_MIC:{success}");
        
                                    // Only restart if it was running before AND selection succeeded
                                    if (success && wasRunning)
                                    {
                                        chatManager.StartMicrophone();
                                    }
                                    // If it wasn't running before, don't start it
                                }
                                break;
                            case "GET_MICS":
                               
                                chatManager.RefreshMicrophones();
                                var mics = chatManager.GetAvailableMicrophones();
                               
                                break;
                            case "EXIT":
                            case "QUIT":
                               
                                cancellationTokenSource.Cancel();
                                Environment.Exit(0); // Force immediate exit
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
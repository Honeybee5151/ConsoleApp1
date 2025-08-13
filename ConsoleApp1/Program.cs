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

        public float MicrophoneGain { get; set; } = 1.5f;
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

        // REPLACE this in your NetworkWorker() method (around line 119):

        private async Task NetworkWorker()
        {
            var messageBuffer = new List<byte[]>(50); // Bigger buffer

            while (!networkCancellation.Token.IsCancellationRequested)
            {
                try
                {
                    // FIXED: Process ALL audio chunks, not just 5
                    messageBuffer.Clear();
            
                    // Process ALL queued audio instead of limiting to 5
                    while (outgoingAudioData.TryDequeue(out var audioData))
                    {
                        messageBuffer.Add(audioData);
                    }

                    // Send ALL audio if any exists
                    if (messageBuffer.Count > 0 && isConnectedToServer)
                    {
                        await SendBatchedAudioToServer(messageBuffer);
                    }

                    // FASTER processing - 10ms instead of 50ms
                    await Task.Delay(10, networkCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"ERROR in NetworkWorker: {ex.Message}");
                    File.AppendAllText("error.log", $"{DateTime.Now}: NetworkWorker - {ex}\n");
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
                    BufferMilliseconds = 50 // Smaller buffers = less choppy
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

        // ULTRA-OPTIMIZED: Absolute minimal processing
        private void OnAudioDataAvailable(object sender, WaveInEventArgs e)
        {
            if (!IsMicrophoneEnabled || waveIn == null) return;
            if (e.BytesRecorded < 100) return;

            audioUpdateCounter++;

            // IMPROVED: Better audio processing with gain
            float maxSample = 0f;
    
            // Process and amplify audio
            for (int i = 0; i < e.BytesRecorded; i += 2) // 16-bit = 2 bytes per sample
            {
                if (i + 1 < e.BytesRecorded)
                {
                    short sample = BitConverter.ToInt16(e.Buffer, i);
            
                    // Apply gain amplification
                    float amplified = sample * MicrophoneGain;
                    amplified = Math.Max(-32767, Math.Min(32767, amplified)); // Prevent clipping
            
                    // Update the buffer with amplified audio
                    var amplifiedShort = (short)amplified;
                    var amplifiedBytes = BitConverter.GetBytes(amplifiedShort);
                    e.Buffer[i] = amplifiedBytes[0];
                    e.Buffer[i + 1] = amplifiedBytes[1];
            
                    // Track levels
                    float abs = Math.Abs(amplified / 32768f);
                    if (abs > maxSample) maxSample = abs;
                }
            }

            float level = maxSample * MicrophoneSensitivity;
            smoothedLevel = smoothedLevel * 0.7f + level * 0.3f; // Faster response

            // Lower noise gate, send more audio
            if (isConnectedToServer && level > 0.005f && allowAudioTransmission) // Much lower threshold
            {
                byte[] audioData = new byte[e.BytesRecorded];
                Array.Copy(e.Buffer, audioData, e.BytesRecorded);
                outgoingAudioData.Enqueue(audioData);
            }

            if (audioUpdateCounter >= 1)
            {
                actionScriptBridge.SendAudioLevel(smoothedLevel);
                audioUpdateCounter = 0;
            }
        }

        private byte[] ConvertToMP3(byte[] pcmData)//d
        {
            using (var memStream = new MemoryStream())
            using (var mp3Writer = new LameMP3FileWriter(memStream,
                       new WaveFormat(44100, 16, 1), 128)) // 44.1kHz + 128kbps instead of 64kbps
            {
                mp3Writer.Write(pcmData, 0, pcmData.Length);
                mp3Writer.Flush();
                return memStream.ToArray();
            }
        }

// 1. ADD this new cleanup function to your ProximityChatManager class:

        private void ForceCleanupCurrentMicrophone()
        {
            try
            {
                Console.WriteLine("DEBUG: Cleaning up current microphone...");
        
                if (waveIn != null)
                {
                    // Remove event handlers first
                    waveIn.DataAvailable -= OnAudioDataAvailable;
                    waveIn.RecordingStopped -= OnRecordingStopped;
            
                    // Stop and dispose
                    try { waveIn.StopRecording(); } catch { }
                    try { waveIn.Dispose(); } catch { }
                    waveIn = null;
                }
        
                // Clear device reference
                if (selectedDevice != null)
                {
                    try { selectedDevice.Dispose(); } catch { }
                    selectedDevice = null;
                }
        
                // Reset state
                IsMicrophoneEnabled = false;
                currentLevel = 0f;
                smoothedLevel = 0f;
                peakLevel = 0f;
        
                // Clear audio queue
                while (outgoingAudioData.TryDequeue(out _)) { }
        
                Console.WriteLine("DEBUG: Microphone cleanup completed");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"DEBUG: Error in cleanup: {ex.Message}");
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
                Console.WriteLine($"ERROR in ConnectToServer: {ex.Message}");
                File.AppendAllText("error.log", $"{DateTime.Now}: ConnectToServer - {ex}\n");
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
        // REPLACE your SendBatchedAudioToServer method with this:
        private async Task SendBatchedAudioToServer(List<byte[]> audioBatch)
        {
            try
            {
                // FIXED: Send ALL audio chunks, not just the last one
                foreach (var audioChunk in audioBatch)
                {
                    var mp3Data = ConvertToMP3(audioChunk);
                    var chatMessage = new ChatMessage
                    {
                        PlayerId = serverId,
                        PlayerName = "LocalPlayer",
                        AudioData = mp3Data,
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

                Console.WriteLine($"DEBUG: Sent {audioBatch.Count} audio chunks to server");
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
                            case "CONNECT_VOICE":
                                if (parts.Length >= 4) // Expects: CONNECT_VOICE:serverIP:playerID:voiceID
                                {
                                    string serverIP = parts[1];
                                    string playerID = parts[2];
                                    string voiceID = parts[3];
                                    Console.WriteLine($"DEBUG: Received CONNECT_VOICE command - ServerIP: {serverIP}, PlayerID: {playerID}, VoiceID: {voiceID}");
                                    _ = chatManager.ConnectToServer(serverIP, 2051, playerID, voiceID);  // ← Use port 2051!
                                }
                                else
                                {
                                    Console.WriteLine("DEBUG: Invalid CONNECT_VOICE command format");
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
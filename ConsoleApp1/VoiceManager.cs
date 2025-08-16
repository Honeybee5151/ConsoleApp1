using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Threading;
using NAudio.Wave;
using System.Text.Json;
using NAudio.CoreAudioApi;
using System.IO;

namespace ConsoleApp1
{
    // Add this class to match the server's AudioMessage format
    public class AudioMessage
    {
        public string Type { get; set; }
        public string AudioData { get; set; } // Base64 encoded
        public float Volume { get; set; }
        public DateTime Timestamp { get; set; }
        public string SpeakerId { get; set; }
    }

    public class VoiceManager : IDisposable
    {
        #region Fields
        
        // REMOVED: UDP components - no longer needed
        // private UdpClient voiceReceiveClient;
        // private IPEndPoint voiceReceiveEndpoint;
        
        private bool isProcessingVoice = false;
        private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        private WaveFormatConversionProvider resampleProvider;

        private string storedServerIP;
        private string storedPlayerID;
        private string storedVoiceID;
        private ProximityChatManager chatManagerRef;
        // Audio output components
        private WaveOutEvent waveOut;
        private BufferedWaveProvider waveProvider;
        
        // Reference to existing components
        private readonly ActionScriptBridge actionScriptBridge;
        
        // TCP voice connection reference (you'll need to pass this in)
        private TcpClient voiceTcpConnection;
        
        // CHANGED: These properties are now for compatibility but not actively used
        public int VoiceReceivePort { get; private set; } = 2051; 
        public bool IsVoiceReceiverActive { get; private set; } = false;
        
        #endregion

        #region Constructor

        public VoiceManager(ActionScriptBridge bridge)
        {
            actionScriptBridge = bridge;
        }

        #endregion

        #region Voice Receiver - TCP Version

        // NEW: Initialize audio components without UDP
       // REPLACE your StartVoiceReceiver method with this adaptive version:

public bool StartVoiceReceiver(int localPort = 2051)
{
    try
    {
        Console.Error.WriteLine("[VOICE_INIT] Starting voice receiver...");
        
        if (IsVoiceReceiverActive)
        {
            Console.Error.WriteLine("[VOICE_INIT] Voice receiver already active");
            return true;
        }

        VoiceReceivePort = localPort;
        
        // PROBE AUDIO SYSTEM FOR ADAPTIVE CONFIGURATION
        var deviceEnumerator = new MMDeviceEnumerator();
        var defaultDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);
        
        string deviceName = defaultDevice.FriendlyName;
        int detectedSampleRate = defaultDevice.AudioClient.MixFormat.SampleRate;
        
        Console.Error.WriteLine($"[VOICE_INIT] Detected_Device: {deviceName}");
        Console.Error.WriteLine($"[VOICE_INIT] Detected_SampleRate: {detectedSampleRate}Hz");
        
        // Initialize audio output with ADAPTIVE settings
        waveOut = new WaveOutEvent();
        
        // ALWAYS use the detected sample rate for output
        WaveFormat outputFormat = new WaveFormat(detectedSampleRate, 16, 1);
        Console.Error.WriteLine($"[VOICE_INIT] Using output format: {outputFormat}");
        
        // Create BufferedWaveProvider with OUTPUT format (his system's native format)
        waveProvider = new BufferedWaveProvider(outputFormat);
        
        // ADAPTIVE BUFFER SIZE based on device type
        if (deviceName.Contains("USB") || deviceName.Contains("Headset") || deviceName.Contains("G432"))
        {
            waveProvider.BufferDuration = TimeSpan.FromMilliseconds(500);
            Console.Error.WriteLine("[VOICE_INIT] Using small buffer for USB/Headset");
        }
        else if (deviceName.Contains("Realtek") || deviceName.Contains("High Definition"))
        {
            waveProvider.BufferDuration = TimeSpan.FromSeconds(2);
            Console.Error.WriteLine("[VOICE_INIT] Using standard buffer for onboard audio");
        }
        else
        {
            waveProvider.BufferDuration = TimeSpan.FromSeconds(1);
            Console.Error.WriteLine("[VOICE_INIT] Using default buffer duration");
        }
        
        waveOut.Init(waveProvider);
        waveOut.Play();
        
        Console.Error.WriteLine($"[VOICE_INIT] WaveOut_State: {waveOut.PlaybackState}");
        Console.Error.WriteLine($"[VOICE_INIT] Output_Format: {outputFormat}");
        Console.Error.WriteLine($"[VOICE_INIT] Buffer_Length: {waveProvider.BufferLength}");
        
        isProcessingVoice = true;
        IsVoiceReceiverActive = true;
        
        Console.Error.WriteLine("[VOICE_INIT] Voice receiver initialization complete");
        return true;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[VOICE_INIT] ERROR: {ex.Message}");
        IsVoiceReceiverActive = false;
        return false;
    }
}
        // NEW: Set the TCP connection for voice
        public void SetVoiceTcpConnection(TcpClient tcpConnection)
        {
            voiceTcpConnection = tcpConnection;
            Console.WriteLine("Voice TCP connection set");
        }

        public void StopVoiceReceiver()
        {
            try
            {
                isProcessingVoice = false;
                IsVoiceReceiverActive = false;
        
                // Stop audio output
                waveOut?.Stop();
                waveOut?.Dispose();
                waveOut = null;
        
                waveProvider = null;
        
                // Clean up resampler
                resampleProvider?.Dispose();
                resampleProvider = null;
        
                voiceTcpConnection = null;
        
                Console.Error.WriteLine("[VOICE_CLEANUP] Voice receiver stopped");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[VOICE_CLEANUP] Error: {ex.Message}");
            }
        }

        // NEW: Process TCP voice messages
      public async Task ProcessVoiceTcpMessage(string jsonMessage)
{
    Console.Error.WriteLine($"🎵 DEBUG: ProcessVoiceTcpMessage called");
    Console.Error.WriteLine($"🎵 DEBUG: Message length: {jsonMessage?.Length ?? 0}");
    Console.Error.WriteLine($"🎵 DEBUG: isProcessingVoice: {isProcessingVoice}");
    
    if (jsonMessage != null && jsonMessage.Length > 0)
    {
        Console.Error.WriteLine($"🎵 DEBUG: First 100 chars: {jsonMessage.Substring(0, Math.Min(100, jsonMessage.Length))}");
    }
    
    if (!isProcessingVoice)
    {
        Console.Error.WriteLine("❌ DEBUG: Not processing voice - system not active");
        return;
    }

    try
    {
        Console.Error.WriteLine("🎵 DEBUG: About to deserialize JSON...");
        
        // Parse the JSON message directly (no prefix)
        var audioMessage = JsonSerializer.Deserialize<AudioMessage>(jsonMessage);
        
        Console.Error.WriteLine($"🎵 DEBUG: Deserialization complete. Type: {audioMessage?.Type}");
        
        if (audioMessage?.Type == "VOICE_AUDIO")
        {
            Console.Error.WriteLine($"🎵 DEBUG: Valid VOICE_AUDIO message from {audioMessage.SpeakerId}");
            Console.Error.WriteLine($"🎵 DEBUG: Volume: {audioMessage.Volume}, AudioData length: {audioMessage.AudioData?.Length ?? 0}");
            
            // Decode the Base64 audio data
            Console.Error.WriteLine("🎵 DEBUG: About to decode Base64...");
            byte[] audioData = Convert.FromBase64String(audioMessage.AudioData);
            Console.Error.WriteLine($"🎵 DEBUG: Decoded {audioData.Length} bytes of audio data");
            
            // Process the audio with the specified volume
            Console.Error.WriteLine("🎵 DEBUG: About to process incoming voice...");
            await ProcessIncomingVoice(audioData, audioMessage.Volume, audioMessage.SpeakerId);
            
            Console.Error.WriteLine($"✅ Processed TCP voice from player {audioMessage.SpeakerId}, {audioData.Length} bytes, volume: {audioMessage.Volume:F2}");
        }
        else
        {
            Console.Error.WriteLine($"❌ DEBUG: Wrong message type: '{audioMessage?.Type}', expected 'VOICE_AUDIO'");
        }
    }
    catch (JsonException jsonEx)
    {
        Console.Error.WriteLine($"❌ JSON Error: {jsonEx.Message}");
        Console.Error.WriteLine($"❌ JSON Error Details: {jsonEx}");
    }
    catch (FormatException formatEx)
    {
        Console.Error.WriteLine($"❌ Base64 Format Error: {formatEx.Message}");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"❌ General Error: {ex.Message}");
        Console.Error.WriteLine($"❌ Exception Type: {ex.GetType().Name}");
        Console.Error.WriteLine($"❌ Stack Trace: {ex.StackTrace}");
    }
}
public void SetChatManagerReference(ProximityChatManager chatManager)
{
    chatManagerRef = chatManager;
}
public void StoreConnectionDetails(string serverIP, string playerID, string voiceID)
{
    storedServerIP = serverIP;
    storedPlayerID = playerID;
    storedVoiceID = voiceID;
}
private void NotifyVoiceReconnect()
{
    try
    {
        if (chatManagerRef != null && !string.IsNullOrEmpty(storedServerIP))
        {
            _ = chatManagerRef.ConnectToServer(storedServerIP, 2051, storedPlayerID, storedVoiceID);
            Console.WriteLine("VoiceManager: Notified chat manager to reconnect");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error notifying voice reconnect: {ex.Message}");
    }
}
private void NotifyVoiceDisconnect()
{
    try
    {
        if (chatManagerRef != null)
        {
            chatManagerRef.DisconnectFromServer();
            Console.WriteLine("VoiceManager: Notified chat manager to disconnect");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"Error notifying voice disconnect: {ex.Message}");
    }
}
private byte[] ResampleAudioIfNeeded(byte[] inputAudioData)
{
    try
    {
        // Input format (what we receive from server)
        WaveFormat inputFormat = new WaveFormat(44100, 16, 1); // Server sends 44.1kHz
        
        // Output format (what our audio system expects)
        WaveFormat outputFormat = waveProvider.WaveFormat;
        
        Console.Error.WriteLine($"🔄 RESAMPLE: Input={inputFormat} → Output={outputFormat}");
        
        // If formats match, no resampling needed
        if (inputFormat.SampleRate == outputFormat.SampleRate)
        {
            Console.Error.WriteLine("🔄 RESAMPLE: No resampling needed - formats match");
            return inputAudioData;
        }
        
        // Create a MemoryStream from input audio
        using (var inputStream = new MemoryStream(inputAudioData))
        {
            // Create WaveFileReader from the raw PCM data
            var rawSource = new RawSourceWaveStream(inputStream, inputFormat);
            
            // Create resampler
            var resampler = new WaveFormatConversionProvider(outputFormat, rawSource);
            
            // Read resampled data
            var outputBuffer = new byte[inputAudioData.Length * outputFormat.SampleRate / inputFormat.SampleRate + 1000]; // Add some padding
            int bytesRead = resampler.Read(outputBuffer, 0, outputBuffer.Length);
            
            // Return only the bytes that were actually read
            byte[] result = new byte[bytesRead];
            Array.Copy(outputBuffer, result, bytesRead);
            
            Console.Error.WriteLine($"🔄 RESAMPLE: Converted {inputAudioData.Length} → {result.Length} bytes");
            
            return result;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"🔄 RESAMPLE_ERROR: {ex.Message}");
        Console.Error.WriteLine("🔄 RESAMPLE: Falling back to original audio");
        
        // If resampling fails, return original audio (better than silence)
        return inputAudioData;
    }
}
        // UPDATED: Process incoming voice with volume and speaker info
      
        private async Task ProcessIncomingVoice(byte[] voiceData, float serverVolume, string speakerId = null)
        {
            try
            {
                Console.Error.WriteLine($"🔊 INCOMING: Processing {voiceData.Length} bytes from {speakerId}");
        
                // Skip processing if local volume is 0
                if (waveOut?.Volume <= 0f)
                {
                    Console.Error.WriteLine("🔊 SKIPPING: Audio is muted");
                    return;
                }

                if (waveProvider != null && voiceData.Length > 0)
                {
                    Console.Error.WriteLine($"🔊 BUFFER: {waveProvider.BufferedBytes}/{waveProvider.BufferLength} before adding {voiceData.Length} bytes");
            
                    // CRITICAL: Resample the audio if needed
                    byte[] finalAudioData = ResampleAudioIfNeeded(voiceData);
            
                    Console.Error.WriteLine($"🔊 RESAMPLE: Input {voiceData.Length} bytes → Output {finalAudioData.Length} bytes");
            
                    // Check buffer space
                    int availableSpace = waveProvider.BufferLength - waveProvider.BufferedBytes;
                    if (availableSpace < finalAudioData.Length)
                    {
                        Console.Error.WriteLine("🔊 WARNING: Buffer full, clearing...");
                        waveProvider.ClearBuffer();
                    }
            
                    // Add the resampled audio
                    waveProvider.AddSamples(finalAudioData, 0, finalAudioData.Length);
            
                    Console.Error.WriteLine($"🔊 SUCCESS: Added {finalAudioData.Length} bytes to audio buffer (volume: {serverVolume:F2})");
                    Console.Error.WriteLine($"🔊 BUFFER_AFTER: {waveProvider.BufferedBytes}/{waveProvider.BufferLength}");
                }
                else
                {
                    Console.Error.WriteLine("🔊 ERROR: waveProvider is null or no audio data");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"🔊 ERROR: {ex.Message}");
                Console.Error.WriteLine($"🔊 STACK: {ex.StackTrace}");
            }
        }

        // LEGACY: Keep old UDP method for backward compatibility but mark as unused
        [Obsolete("UDP voice reception is no longer used. Voice is now handled via TCP.")]
        private async Task ListenForIncomingVoice()
        {
            // This method is no longer used but kept for compatibility
            Console.WriteLine("UDP voice listening is deprecated - using TCP voice instead");
        }

        #endregion

        #region Public Methods

        public string GetLocalEndpoint()
        {
            try
            {
                // Get local IP by connecting to a remote address
                using (var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, 0))
                {
                    socket.Connect("8.8.8.8", 65530);
                    var endPoint = socket.LocalEndPoint as IPEndPoint;
                    return $"{endPoint.Address}:{VoiceReceivePort}";
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting local endpoint: {ex.Message}");
                return $"127.0.0.1:{VoiceReceivePort}";
            }
        }

        public void SetIncomingVolume(float volume)
        {
            try
            {
                float clampedVolume = Math.Max(0f, Math.Min(1f, volume));
        
                if (waveOut != null)
                {
                    waveOut.Volume = clampedVolume;
                }
        
                // NEW: Complete disconnect when volume is 0
                if (clampedVolume <= 0f)
                {
                    Console.WriteLine("VoiceManager: Volume set to 0 - disconnecting from voice system");
                    DisconnectFromVoiceSystem();
                }
                else if (clampedVolume > 0f && !IsVoiceReceiverActive)
                {
                    Console.WriteLine("VoiceManager: Volume restored - reconnecting to voice system");
                    ReconnectToVoiceSystem();
                }
        
                Console.WriteLine($"VoiceManager: Set incoming volume to {clampedVolume:F2}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting incoming volume: {ex.Message}");
            }
        }
        private void DisconnectFromVoiceSystem()
        {
            try
            {
                // Stop processing voice entirely
                StopVoiceReceiver();

                // NEW: Send disconnect message to server
                if (chatManagerRef != null && !string.IsNullOrEmpty(storedPlayerID))
                {
                    SendVoiceDisconnectToServer();
                }

                // Notify the chat manager to disconnect this client from voice server
                NotifyVoiceDisconnect();

                Console.WriteLine("VoiceManager: Completely disconnected from voice system");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error disconnecting from voice system: {ex.Message}");
            }
        }
        private void SendVoiceDisconnectToServer()
        {
            try
            {
                // Send VOICE_DISCONNECT message through the chat manager's connection
                var disconnectMessage = $"VOICE_DISCONNECT:{storedPlayerID}";
        
                // You'll need to add this method to ProximityChatManager
                _ = Task.Run(async () => await chatManagerRef.SendServerMessage(disconnectMessage));
        
                Console.WriteLine($"Sent VOICE_DISCONNECT for player {storedPlayerID}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error sending voice disconnect: {ex.Message}");
            }
        }
        private void ReconnectToVoiceSystem()
        {
            try
            {
                // Restart voice receiver
                if (StartVoiceReceiver())
                {
                    // Notify the chat manager to reconnect to voice server
                    NotifyVoiceReconnect();
            
                    Console.WriteLine("VoiceManager: Successfully reconnected to voice system");
                }
                else
                {
                    Console.WriteLine("VoiceManager: Failed to reconnect to voice system");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error reconnecting to voice system: {ex.Message}");
            }
        }
        public float GetIncomingVolume()
        {
            try
            {
                return waveOut?.Volume ?? 0f;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error getting incoming volume: {ex.Message}");
                return 0f;
            }
        }

        public float GetCurrentVolume()
        {
            return waveOut?.Volume ?? 0f;
        }

        public void SetVolume(float volume)
        {
            if (waveOut != null)
            {
                waveOut.Volume = Math.Max(0f, Math.Min(1f, volume));
            }
        }

        // NEW: Helper method to check if voice system is ready
        public bool IsVoiceSystemReady()
        {
            return IsVoiceReceiverActive && waveOut != null && waveProvider != null;
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            StopVoiceReceiver();
            cancellationTokenSource.Cancel();
            cancellationTokenSource.Dispose();
        }

        #endregion
    }
}
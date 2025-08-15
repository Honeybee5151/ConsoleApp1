using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Threading;
using NAudio.Wave;
using System.Text.Json;

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
        public bool StartVoiceReceiver(int localPort = 2051)
        {
            try
            {
                if (IsVoiceReceiverActive)
                {
                    Console.WriteLine("Voice receiver already active");
                    return true;
                }

                VoiceReceivePort = localPort; // Keep for compatibility
                
                // Initialize audio output
                waveOut = new WaveOutEvent();
                waveProvider = new BufferedWaveProvider(new WaveFormat(44100, 16, 1));
                waveProvider.BufferDuration = TimeSpan.FromSeconds(2); // 2 second buffer
                waveOut.Init(waveProvider);
                waveOut.Play();
                
                isProcessingVoice = true;
                IsVoiceReceiverActive = true;
                
                Console.WriteLine($"Voice receiver initialized (TCP mode)");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error starting voice receiver: {ex.Message}");
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
                
                // Don't close the TCP connection here - it's managed elsewhere
                voiceTcpConnection = null;
                
                Console.WriteLine("Voice receiver stopped");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error stopping voice receiver: {ex.Message}");
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

        // UPDATED: Process incoming voice with volume and speaker info
        private async Task ProcessIncomingVoice(byte[] voiceData, float serverVolume, string speakerId = null)
        {
            try
            {
                // Skip processing if local volume is 0 (performance optimization)
                if (waveOut?.Volume <= 0f)
                {
                    // Audio is muted, don't process to save CPU
                    return;
                }

                // Apply server-specified volume to the local volume
                float effectiveVolume = waveOut.Volume * serverVolume;
                
                // Temporarily adjust volume for this audio if needed
                float originalVolume = waveOut.Volume;
                if (Math.Abs(serverVolume - 1.0f) > 0.01f) // If server volume is different from 1.0
                {
                    waveOut.Volume = Math.Max(0f, Math.Min(1f, effectiveVolume));
                }
        
                // Voice data from server is processed PCM - play it directly
                if (waveProvider != null && voiceData.Length > 0)
                {
                    waveProvider.AddSamples(voiceData, 0, voiceData.Length);
                    Console.WriteLine($"Added {voiceData.Length} bytes to audio buffer (effective volume: {effectiveVolume:F2})");
                }

                // Restore original volume after a short delay (optional)
                if (Math.Abs(serverVolume - 1.0f) > 0.01f)
                {
                    _ = Task.Delay(100).ContinueWith(_ => 
                    {
                        if (waveOut != null)
                            waveOut.Volume = originalVolume;
                    });
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing incoming voice: {ex.Message}");
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
                if (waveOut != null)
                {
                    waveOut.Volume = Math.Max(0f, Math.Min(1f, volume));
                    Console.WriteLine($"VoiceManager: Set incoming volume to {volume:F2}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error setting incoming volume: {ex.Message}");
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
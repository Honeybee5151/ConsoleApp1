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
    // UDP Voice packet structure
    public class UdpVoicePacket
    {
        public string SpeakerId { get; set; }
        public float Volume { get; set; }
        public byte[] OpusAudioData { get; set; }
        public DateTime Timestamp { get; set; }
    }

    public class VoiceManager : IDisposable
    {
        #region Fields

        private OpusAudioProcessor opusProcessor;
        private UdpClient udpReceiveClient;
        private IPEndPoint serverEndpoint;

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

        // UDP voice properties
        public int VoiceReceivePort { get; private set; } = 2051;
        public bool IsVoiceReceiverActive { get; private set; } = false;

        // Device-specific settings
        private WaveFormat detectedOutputFormat;
        private string detectedDeviceName;

        #endregion

        #region Constructor

        public VoiceManager(ActionScriptBridge bridge)
        {
            actionScriptBridge = bridge;
            opusProcessor = new OpusAudioProcessor();
        }

        #endregion

        #region UDP Voice Receiver

        public bool StartVoiceReceiver(int localPort = 2051)
        {
            try
            {
                Console.Error.WriteLine("[UDP_VOICE_INIT] Starting UDP voice receiver...");

                if (IsVoiceReceiverActive)
                {
                    Console.Error.WriteLine("[UDP_VOICE_INIT] Voice receiver already active");
                    return true;
                }

                VoiceReceivePort = localPort;

                // PROBE AUDIO SYSTEM FOR ADAPTIVE CONFIGURATION
                var deviceEnumerator = new MMDeviceEnumerator();
                var defaultDevice = deviceEnumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Communications);

                detectedDeviceName = defaultDevice.FriendlyName;
                int detectedSampleRate = defaultDevice.AudioClient.MixFormat.SampleRate;

                Console.Error.WriteLine($"[UDP_VOICE_INIT] Detected_Device: {detectedDeviceName}");
                Console.Error.WriteLine($"[UDP_VOICE_INIT] Detected_SampleRate: {detectedSampleRate}Hz");

                // Initialize audio output with ADAPTIVE settings
                waveOut = new WaveOutEvent();

                // ALWAYS use the detected sample rate for output
                detectedOutputFormat = new WaveFormat(detectedSampleRate, 16, 1);
                Console.Error.WriteLine($"[UDP_VOICE_INIT] Using output format: {detectedOutputFormat}");

                // Create BufferedWaveProvider with OUTPUT format (user's system native format)
                waveProvider = new BufferedWaveProvider(detectedOutputFormat);

                // ADAPTIVE BUFFER SIZE based on device type
                if (detectedDeviceName.Contains("USB") || detectedDeviceName.Contains("Headset") ||
                    detectedDeviceName.Contains("G432"))
                {
                    waveProvider.BufferDuration = TimeSpan.FromMilliseconds(500);
                    Console.Error.WriteLine("[UDP_VOICE_INIT] Using small buffer for USB/Headset");
                }
                else if (detectedDeviceName.Contains("Realtek") || detectedDeviceName.Contains("High Definition"))
                {
                    waveProvider.BufferDuration = TimeSpan.FromSeconds(2);
                    Console.Error.WriteLine("[UDP_VOICE_INIT] Using standard buffer for onboard audio");
                }
                else
                {
                    waveProvider.BufferDuration = TimeSpan.FromSeconds(1);
                    Console.Error.WriteLine("[UDP_VOICE_INIT] Using default buffer duration");
                }

                waveOut.Init(waveProvider);
                waveOut.Play();

                Console.Error.WriteLine($"[UDP_VOICE_INIT] WaveOut_State: {waveOut.PlaybackState}");
                Console.Error.WriteLine($"[UDP_VOICE_INIT] Output_Format: {detectedOutputFormat}");
                Console.Error.WriteLine($"[UDP_VOICE_INIT] Buffer_Length: {waveProvider.BufferLength}");

                // Initialize UDP client for receiving
                udpReceiveClient = new UdpClient(localPort);

                isProcessingVoice = true;
                IsVoiceReceiverActive = true;

                // Start UDP listening task
                _ = Task.Run(UdpVoiceListener, cancellationTokenSource.Token);

                Console.Error.WriteLine("[UDP_VOICE_INIT] UDP voice receiver initialization complete");
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[UDP_VOICE_INIT] ERROR: {ex.Message}");
                IsVoiceReceiverActive = false;
                return false;
            }
        }

        private async Task UdpVoiceListener()
        {
            Console.Error.WriteLine("[UDP_LISTENER] Starting UDP voice packet listener");

            while (isProcessingVoice && !cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await udpReceiveClient.ReceiveAsync();
                    var packet = result.Buffer;
                    var senderEndpoint = result.RemoteEndPoint;

                    Console.Error.WriteLine($"[UDP_LISTENER] Received {packet.Length} bytes from {senderEndpoint}");

                    // Process different packet types
                    if (packet.Length >= 4)
                    {
                        string packetType = System.Text.Encoding.UTF8.GetString(packet, 0, 4);

                        if (packetType == "ARSP") // Auth response
                        {
                            await ProcessAuthResponse(packet);
                            continue;
                        }
                        else if (packetType == "PRSP") // Priority response
                        {
                            await ProcessPriorityResponse(packet);
                            continue;
                        }
                        else if (packetType == "PONG") // Ping response
                        {
                            Console.Error.WriteLine("[UDP_LISTENER] Received PONG from server");
                            continue;
                        }
                    }

                    // Voice data packet: [16 bytes speakerId][4 bytes volume][Opus audio]
                    if (packet.Length >= 20)
                    {
                        await ProcessUdpVoicePacket(packet, senderEndpoint);
                    }
                }
                catch (Exception ex)
                {
                    if (!cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        Console.Error.WriteLine($"[UDP_LISTENER] Error: {ex.Message}");
                        await Task.Delay(100, cancellationTokenSource.Token);
                    }
                }
            }

            Console.Error.WriteLine("[UDP_LISTENER] UDP voice listener stopped");
        }

        private async Task ProcessUdpVoicePacket(byte[] packet, IPEndPoint senderEndpoint)
        {
            try
            {
                // Extract speaker ID (16 bytes)
                string speakerId = System.Text.Encoding.UTF8.GetString(packet, 0, 16).Trim('\0');

                // Extract volume (4 bytes)
                float serverVolume = BitConverter.ToSingle(packet, 16);

                // Extract Opus audio data (remaining bytes)
                byte[] opusAudioData = new byte[packet.Length - 20];
                Array.Copy(packet, 20, opusAudioData, 0, opusAudioData.Length);

                Console.Error.WriteLine(
                    $"[UDP_VOICE] Received voice from {speakerId}: {opusAudioData.Length} Opus bytes, volume: {serverVolume:F2}");

                // Process the voice with full pipeline
                await ProcessIncomingUdpVoice(opusAudioData, serverVolume, speakerId);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[UDP_VOICE] Error processing voice packet: {ex.Message}");
            }
        }

        private async Task ProcessIncomingUdpVoice(byte[] opusAudioData, float serverVolume, string speakerId)
        {
            try
            {
                Console.Error.WriteLine(
                    $"🔊 UDP_INCOMING: Processing {opusAudioData.Length} Opus bytes from {speakerId}");

                if (waveOut?.Volume <= 0f)
                {
                    Console.Error.WriteLine("🔊 SKIPPING: Audio is muted");
                    return;
                }

                // STEP 1: DECODE Opus → Raw PCM (48kHz)
                byte[] rawPcmData = DecodeOpusToRawPcm(opusAudioData);
                if (rawPcmData.Length == 0)
                {
                    Console.Error.WriteLine("🔊 ERROR: Opus decoding failed");
                    return;
                }

                Console.Error.WriteLine(
                    $"🔊 DECODED: {opusAudioData.Length} Opus bytes → {rawPcmData.Length} PCM bytes");

                // STEP 2: RESAMPLE to match user's device (48kHz → device native rate)
                byte[] finalAudioData = ResampleToDeviceFormat(rawPcmData);

                Console.Error.WriteLine(
                    $"🔊 RESAMPLED: {rawPcmData.Length} bytes → {finalAudioData.Length} bytes for {detectedOutputFormat}");

                // STEP 3: APPLY server-calculated volume
                byte[] volumeAdjustedAudio = ApplyVolumeToAudio(finalAudioData, serverVolume);

                // STEP 4: ADD to playback buffer
                if (waveProvider != null && volumeAdjustedAudio.Length > 0)
                {
                    // Check buffer space
                    int availableSpace = waveProvider.BufferLength - waveProvider.BufferedBytes;
                    if (availableSpace < volumeAdjustedAudio.Length)
                    {
                        Console.Error.WriteLine("🔊 WARNING: Buffer full, clearing...");
                        waveProvider.ClearBuffer();
                    }

                    waveProvider.AddSamples(volumeAdjustedAudio, 0, volumeAdjustedAudio.Length);
                    Console.Error.WriteLine(
                        $"🔊 SUCCESS: Added {volumeAdjustedAudio.Length} bytes to audio buffer from {speakerId}");
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"🔊 ERROR processing UDP voice: {ex.Message}");
            }
        }

        private byte[] DecodeOpusToRawPcm(byte[] opusData)
        {
            try
            {
                if (opusProcessor == null)
                {
                    Console.Error.WriteLine("🔊 WARNING: No Opus processor - returning empty audio");
                    return new byte[0];
                }

                // Decode Opus to PCM (48kHz, 16-bit, mono)
                byte[] rawPcm = opusProcessor.DecodeFromOpus(opusData);

                if (rawPcm.Length > 0)
                {
                    Console.Error.WriteLine($"🔊 OPUS_DECODE: {opusData.Length} → {rawPcm.Length} bytes (48kHz PCM)");
                }
                else
                {
                    Console.Error.WriteLine("🔊 OPUS_DECODE: Failed to decode Opus data");
                }

                return rawPcm;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"🔊 OPUS_DECODE_ERROR: {ex.Message}");
                return new byte[0];
            }
        }

        private byte[] ResampleToDeviceFormat(byte[] pcm48khzData)
        {
            try
            {
                // Input format: 48kHz (what Opus decoder outputs)
                WaveFormat inputFormat = new WaveFormat(48000, 16, 1);

                // Output format: User's device native format
                WaveFormat outputFormat = detectedOutputFormat;

                Console.Error.WriteLine($"🔄 RESAMPLE: {inputFormat} → {outputFormat}");

                // If formats match, no resampling needed
                if (inputFormat.SampleRate == outputFormat.SampleRate)
                {
                    Console.Error.WriteLine("🔄 RESAMPLE: No resampling needed - formats match");
                    return pcm48khzData;
                }

                // Create a MemoryStream from input audio
                using (var inputStream = new MemoryStream(pcm48khzData))
                {
                    var rawSource = new RawSourceWaveStream(inputStream, inputFormat);
                    var resampler = new WaveFormatConversionProvider(outputFormat, rawSource);

                    // Calculate expected output size
                    int expectedOutputSize =
                        (int)((long)pcm48khzData.Length * outputFormat.SampleRate / inputFormat.SampleRate) + 1000;
                    var outputBuffer = new byte[expectedOutputSize];

                    int bytesRead = resampler.Read(outputBuffer, 0, outputBuffer.Length);

                    // Return only the bytes that were actually read
                    byte[] result = new byte[bytesRead];
                    Array.Copy(outputBuffer, result, bytesRead);

                    Console.Error.WriteLine($"🔄 RESAMPLE: Converted {pcm48khzData.Length} → {result.Length} bytes");

                    return result;
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"🔄 RESAMPLE_ERROR: {ex.Message}");
                Console.Error.WriteLine("🔄 RESAMPLE: Falling back to original audio");

                // If resampling fails, return original audio (better than silence)
                return pcm48khzData;
            }
        }

        private byte[] ApplyVolumeToAudio(byte[] audioData, float volumeMultiplier)
        {
            try
            {
                // Clamp volume to reasonable range
                volumeMultiplier = Math.Max(0.0f, Math.Min(2.0f, volumeMultiplier));

                if (Math.Abs(volumeMultiplier - 1.0f) < 0.001f)
                {
                    // No volume change needed
                    return audioData;
                }

                byte[] adjustedAudio = new byte[audioData.Length];

                // Process 16-bit samples
                for (int i = 0; i < audioData.Length - 1; i += 2)
                {
                    // Convert bytes to 16-bit sample
                    short sample = BitConverter.ToInt16(audioData, i);

                    // Apply volume
                    float adjustedSample = sample * volumeMultiplier;

                    // Clamp to prevent clipping
                    adjustedSample = Math.Max(-32768, Math.Min(32767, adjustedSample));

                    // Convert back to bytes
                    byte[] sampleBytes = BitConverter.GetBytes((short)adjustedSample);
                    adjustedAudio[i] = sampleBytes[0];
                    adjustedAudio[i + 1] = sampleBytes[1];
                }

                Console.Error.WriteLine($"🔊 VOLUME: Applied {volumeMultiplier:F2}x volume adjustment");
                return adjustedAudio;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"🔊 VOLUME_ERROR: {ex.Message}");
                return audioData; // Return original if volume adjustment fails
            }
        }

        private async Task ProcessAuthResponse(byte[] packet)
        {
            try
            {
                string jsonData = System.Text.Encoding.UTF8.GetString(packet, 4, packet.Length - 4);
                var response = JsonSerializer.Deserialize<dynamic>(jsonData);
                Console.Error.WriteLine($"[UDP_AUTH] Server response: {jsonData}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[UDP_AUTH] Error processing auth response: {ex.Message}");
            }
        }

        private async Task ProcessPriorityResponse(byte[] packet)
        {
            try
            {
                string jsonData = System.Text.Encoding.UTF8.GetString(packet, 4, packet.Length - 4);
                var response = JsonSerializer.Deserialize<dynamic>(jsonData);
                Console.Error.WriteLine($"[UDP_PRIORITY] Server response: {jsonData}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[UDP_PRIORITY] Error processing priority response: {ex.Message}");
            }
        }

        public async Task SendAuthenticationToServer(string serverIP, int port, string playerId, string voiceId)
        {
            try
            {
                serverEndpoint = new IPEndPoint(IPAddress.Parse(serverIP), port);

                var authRequest = new ProximityChatManager.UdpAuthRequest
                {
                    PlayerId = playerId,
                    VoiceId = voiceId,
                    Command = "AUTH"
                };

                string jsonData = JsonSerializer.Serialize(authRequest);
                byte[] authPacket = System.Text.Encoding.UTF8.GetBytes("AUTH" + jsonData);

                await udpReceiveClient.SendAsync(authPacket, authPacket.Length, serverEndpoint);
                Console.Error.WriteLine($"[UDP_AUTH] Sent authentication for player {playerId}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[UDP_AUTH] Error sending authentication: {ex.Message}");
            }
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

                // Stop UDP client
                udpReceiveClient?.Close();
                udpReceiveClient?.Dispose();
                udpReceiveClient = null;

                // Clean up resampler
                resampleProvider?.Dispose();
                resampleProvider = null;

                Console.Error.WriteLine("[UDP_VOICE_CLEANUP] Voice receiver stopped");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[UDP_VOICE_CLEANUP] Error: {ex.Message}");
            }
        }

        #endregion

        #region Existing Methods (adapted for UDP)

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

        public string GetLocalEndpoint()
        {
            try
            {
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

                Console.WriteLine($"UDP VoiceManager: Set incoming volume to {clampedVolume:F2}");
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

        public bool IsVoiceSystemReady()
        {
            return IsVoiceReceiverActive && waveOut != null && waveProvider != null && udpReceiveClient != null;
        }

        #endregion

        #region IDisposable

        public void Dispose()
        {
            StopVoiceReceiver();
            cancellationTokenSource.Cancel();
            cancellationTokenSource.Dispose();

            // OpusAudioProcessor disposal (if it implements IDisposable)
            try
            {
                if (opusProcessor is IDisposable disposableOpus)
                {
                    disposableOpus.Dispose();
                }
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error disposing Opus processor: {ex.Message}");
            }
        }

        #endregion
    }
}
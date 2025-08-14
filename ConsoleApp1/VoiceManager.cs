using System;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using System.Threading;
using NAudio.Wave;

namespace ConsoleApp1
{
    public class VoiceManager : IDisposable
    {
        #region Fields
        
        private UdpClient voiceReceiveClient;
        private IPEndPoint voiceReceiveEndpoint;
        private bool isListeningForVoice = false;
        private Task voiceListeningTask;
        private readonly CancellationTokenSource cancellationTokenSource = new CancellationTokenSource();
        
        // Audio output components
        private WaveOutEvent waveOut;
        private BufferedWaveProvider waveProvider;
        
        // Reference to existing components
        private readonly ActionScriptBridge actionScriptBridge;
        
        public int VoiceReceivePort { get; private set; } = 2051; // CHANGED: Use 2051
        public bool IsVoiceReceiverActive { get; private set; } = false;
        
        #endregion

        #region Constructor

        public VoiceManager(ActionScriptBridge bridge)
        {
            actionScriptBridge = bridge;
        }

        #endregion

        #region Voice Receiver

        public bool StartVoiceReceiver(int localPort = 2051) // CHANGED: Default to 2051
        {
            try
            {
                if (IsVoiceReceiverActive)
                {
                    Console.WriteLine("Voice receiver already active");
                    return true;
                }

                VoiceReceivePort = localPort;
                voiceReceiveEndpoint = new IPEndPoint(IPAddress.Any, localPort);
                voiceReceiveClient = new UdpClient(voiceReceiveEndpoint);
                
                // Initialize audio output
                waveOut = new WaveOutEvent();
                waveProvider = new BufferedWaveProvider(new WaveFormat(44100, 16, 1));
                waveProvider.BufferDuration = TimeSpan.FromSeconds(2); // 2 second buffer
                waveOut.Init(waveProvider);
                waveOut.Play();
                
                isListeningForVoice = true;
                IsVoiceReceiverActive = true;
                voiceListeningTask = Task.Run(ListenForIncomingVoice, cancellationTokenSource.Token);
                
                Console.WriteLine($"Voice receiver started on port {localPort}");
                return true;
            }
            catch (SocketException ex) // ADDED: Better error handling for port conflicts
            {
                Console.WriteLine($"Voice receiver failed - port {localPort} already in use");
                Console.WriteLine("Another instance may already be running on this computer");
                IsVoiceReceiverActive = false;
                return false; // Natural anti-multiboxing
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error starting voice receiver: {ex.Message}");
                IsVoiceReceiverActive = false;
                return false;
            }
        }

        public void StopVoiceReceiver()
        {
            try
            {
                isListeningForVoice = false;
                IsVoiceReceiverActive = false;
                
                // Stop audio output
                waveOut?.Stop();
                waveOut?.Dispose();
                waveOut = null;
                waveProvider = null;
                
                // Stop UDP receiver
                voiceReceiveClient?.Close();
                voiceReceiveClient?.Dispose();
                voiceReceiveClient = null;
                
                Console.WriteLine("Voice receiver stopped");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error stopping voice receiver: {ex.Message}");
            }
        }

        private async Task ListenForIncomingVoice()
        {
            while (isListeningForVoice && !cancellationTokenSource.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await voiceReceiveClient.ReceiveAsync();
                    await ProcessIncomingVoice(result.Buffer);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (isListeningForVoice)
                    {
                        Console.WriteLine($"Error receiving voice: {ex.Message}");
                        await Task.Delay(100, cancellationTokenSource.Token);
                    }
                }
            }
        }

        private async Task ProcessIncomingVoice(byte[] voiceData)
        {
            try
            {
                // Skip processing if volume is 0 (performance optimization)
                if (waveOut?.Volume <= 0f)
                {
                    // Audio is muted, don't process to save CPU
                    return;
                }
        
                // Voice data from server is processed PCM - play it directly
                if (waveProvider != null && voiceData.Length > 0)
                {
                    waveProvider.AddSamples(voiceData, 0, voiceData.Length);
                    Console.WriteLine($"Added {voiceData.Length} bytes to audio buffer");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error processing incoming voice: {ex.Message}");
            }
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

        #endregion

        #region IDisposable

        public void Dispose()
        {
            StopVoiceReceiver();
            cancellationTokenSource.Cancel();
            
            try
            {
                voiceListeningTask?.Wait(1000);
            }
            catch { }
            
            cancellationTokenSource.Dispose();
        }

        #endregion
    }
}
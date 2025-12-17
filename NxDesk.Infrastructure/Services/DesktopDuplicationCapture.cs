using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace NxDesk.Infrastructure.Services
{
    /// <summary>
    /// Captura de pantalla usando Desktop Duplication API (DXGI).
    /// Mucho más eficiente que GDI BitBlt - usa GPU directamente.
    /// </summary>
    public class DesktopDuplicationCapture : IDisposable
    {
        private Device _device;
        private OutputDuplication _duplicatedOutput;
        private Texture2D _screenTexture;
        private int _width;
        private int _height;
        private bool _initialized = false;
        private int _currentAdapterIndex = 0;
        private int _currentOutputIndex = 0;
        
        // Buffer reutilizable para evitar allocations
        private byte[] _frameBuffer;

        public int Width => _width;
        public int Height => _height;
        public bool IsInitialized => _initialized;

        /// <summary>
        /// Inicializa la captura para un monitor específico.
        /// </summary>
        /// <param name="monitorIndex">Índice del monitor (0 = primario)</param>
        public bool Initialize(int monitorIndex = 0)
        {
            try
            {
                Dispose(); // Limpiar recursos anteriores

                using var factory = new Factory1();
                
                // Encontrar el adapter y output correctos
                int outputCount = 0;
                for (int adapterIndex = 0; adapterIndex < factory.GetAdapterCount1(); adapterIndex++)
                {
                    using var adapter = factory.GetAdapter1(adapterIndex);
                    
                    for (int outputIndex = 0; outputIndex < adapter.GetOutputCount(); outputIndex++)
                    {
                        if (outputCount == monitorIndex)
                        {
                            _currentAdapterIndex = adapterIndex;
                            _currentOutputIndex = outputIndex;
                            break;
                        }
                        outputCount++;
                    }
                }

                // Crear device con el adapter seleccionado
                using var selectedAdapter = factory.GetAdapter1(_currentAdapterIndex);
                _device = new Device(selectedAdapter, DeviceCreationFlags.BgraSupport);

                using var output = selectedAdapter.GetOutput(_currentOutputIndex);
                using var output1 = output.QueryInterface<Output1>();

                var bounds = output.Description.DesktopBounds;
                _width = bounds.Right - bounds.Left;
                _height = bounds.Bottom - bounds.Top;

                // Asegurar dimensiones pares para VP8
                if (_width % 2 != 0) _width--;
                if (_height % 2 != 0) _height--;

                // Crear textura para copiar la pantalla
                var textureDesc = new Texture2DDescription
                {
                    CpuAccessFlags = CpuAccessFlags.Read,
                    BindFlags = BindFlags.None,
                    Format = Format.B8G8R8A8_UNorm,
                    Width = _width,
                    Height = _height,
                    OptionFlags = ResourceOptionFlags.None,
                    MipLevels = 1,
                    ArraySize = 1,
                    SampleDescription = { Count = 1, Quality = 0 },
                    Usage = ResourceUsage.Staging
                };
                _screenTexture = new Texture2D(_device, textureDesc);

                // Duplicar output
                _duplicatedOutput = output1.DuplicateOutput(_device);
                
                // Pre-allocar buffer
                _frameBuffer = new byte[_width * _height * 4];

                _initialized = true;
                Debug.WriteLine($"[DXGI] Inicializado: {_width}x{_height}, Monitor {monitorIndex}");
                return true;
            }
            catch (SharpDXException ex) when (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.NotCurrentlyAvailable.Code)
            {
                Debug.WriteLine("[DXGI] Error: Desktop Duplication no disponible (¿otra app lo está usando?)");
                return false;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DXGI] Error inicializando: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Captura un frame. Retorna null si no hay cambios o hay error.
        /// </summary>
        /// <param name="timeoutMs">Timeout en milisegundos</param>
        public byte[] CaptureFrame(int timeoutMs = 100)
        {
            if (!_initialized) return null;

            SharpDX.DXGI.Resource screenResource = null;
            
            try
            {
                // Intentar adquirir el siguiente frame
                var result = _duplicatedOutput.TryAcquireNextFrame(timeoutMs, out var frameInfo, out screenResource);
                
                if (result.Failure)
                {
                    // Timeout o error - no hay frame nuevo
                    return null;
                }

                // Si no hay cambios en la pantalla, podemos saltar
                if (frameInfo.TotalMetadataBufferSize == 0 && frameInfo.LastPresentTime == 0)
                {
                    _duplicatedOutput.ReleaseFrame();
                    return null;
                }

                using var screenTexture2D = screenResource.QueryInterface<Texture2D>();
                
                // Copiar a nuestra textura staging
                _device.ImmediateContext.CopyResource(screenTexture2D, _screenTexture);

                // Mapear la textura para leer los píxeles
                var dataBox = _device.ImmediateContext.MapSubresource(
                    _screenTexture, 
                    0, 
                    MapMode.Read, 
                    MapFlags.None);

                try
                {
                    var sourcePtr = dataBox.DataPointer;
                    int rowPitch = dataBox.RowPitch;
                    
                    // Copiar fila por fila (por si hay padding)
                    for (int y = 0; y < _height; y++)
                    {
                        Marshal.Copy(
                            sourcePtr + y * rowPitch,
                            _frameBuffer,
                            y * _width * 4,
                            _width * 4);
                    }
                    
                    return _frameBuffer;
                }
                finally
                {
                    _device.ImmediateContext.UnmapSubresource(_screenTexture, 0);
                }
            }
            catch (SharpDXException ex) when (ex.ResultCode.Code == SharpDX.DXGI.ResultCode.AccessLost.Code)
            {
                Debug.WriteLine("[DXGI] Acceso perdido, reinicializando...");
                _initialized = false;
                return null;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DXGI] Error capturando: {ex.Message}");
                return null;
            }
            finally
            {
                try
                {
                    screenResource?.Dispose();
                    _duplicatedOutput?.ReleaseFrame();
                }
                catch { }
            }
        }

        /// <summary>
        /// Captura a un Bitmap (para compatibilidad con código existente).
        /// </summary>
        public Bitmap CaptureToBitmap(int timeoutMs = 100)
        {
            var pixels = CaptureFrame(timeoutMs);
            if (pixels == null) return null;

            try
            {
                var bitmap = new Bitmap(_width, _height, PixelFormat.Format32bppArgb);
                var bmpData = bitmap.LockBits(
                    new Rectangle(0, 0, _width, _height),
                    ImageLockMode.WriteOnly,
                    PixelFormat.Format32bppArgb);

                try
                {
                    Marshal.Copy(pixels, 0, bmpData.Scan0, pixels.Length);
                }
                finally
                {
                    bitmap.UnlockBits(bmpData);
                }

                return bitmap;
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[DXGI] Error creando bitmap: {ex.Message}");
                return null;
            }
        }

        public void Dispose()
        {
            _initialized = false;
            
            try { _duplicatedOutput?.Dispose(); } catch { }
            try { _screenTexture?.Dispose(); } catch { }
            try { _device?.Dispose(); } catch { }
            
            _duplicatedOutput = null;
            _screenTexture = null;
            _device = null;
        }
    }
}

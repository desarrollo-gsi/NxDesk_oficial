namespace NxDesk.Application.DTOs
{
    /// <summary>
    /// Representa un frame de video con píxeles raw BGRA.
    /// Optimizado para uso con WriteableBitmap.
    /// </summary>
    public class RawVideoFrame
    {
        /// <summary>
        /// Píxeles en formato BGRA (4 bytes por pixel).
        /// </summary>
        public byte[] Pixels { get; set; }
        
        /// <summary>
        /// Ancho en píxeles.
        /// </summary>
        public int Width { get; set; }
        
        /// <summary>
        /// Alto en píxeles.
        /// </summary>
        public int Height { get; set; }
        
        /// <summary>
        /// Stride (bytes por fila). Normalmente Width * 4 para BGRA.
        /// </summary>
        public int Stride => Width * 4;
        
        public RawVideoFrame(byte[] pixels, int width, int height)
        {
            Pixels = pixels;
            Width = width;
            Height = height;
        }
    }
}

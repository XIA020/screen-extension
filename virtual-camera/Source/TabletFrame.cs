using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using DirectN;

namespace VCamNetSampleSource;

internal sealed class TabletFrame
{
    private readonly byte[] pixels = new byte[MediaStream.NUM_IMAGE_COLS * MediaStream.NUM_IMAGE_ROWS * 4];
    private long nextRefresh;

    public void CopyTo(IMFSample sample)
    {
        long now = Environment.TickCount64;
        if (now >= nextRefresh)
        {
            nextRefresh = now + 90;
            Refresh();
        }
        using var buffer = MFFunctions.MFCreateMemoryBuffer((uint)pixels.Length);
        buffer.WithLock((pointer, length, _) => Marshal.Copy(pixels, 0, pointer, pixels.Length));
        buffer.SetCurrentLength((uint)pixels.Length);
        sample.RemoveAllBuffers().ThrowOnError();
        sample.AddBuffer(buffer.Object).ThrowOnError();
    }

    private void Refresh()
    {
        Array.Clear(pixels);
        try
        {
            using var client = new TcpClient();
            if (!client.ConnectAsync("127.0.0.1", 47997).Wait(150))
                return;
            client.ReceiveTimeout = 150;
            using var input = new BinaryReader(client.GetStream());
            int count = input.ReadInt32();
            if (count <= 0 || count > 512 * 1024)
                return;
            using var data = new MemoryStream(input.ReadBytes(count));
            using var frame = Image.FromStream(data);
            using var bitmap = new Bitmap(MediaStream.NUM_IMAGE_COLS, MediaStream.NUM_IMAGE_ROWS, PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.Clear(Color.Black);
                double scale = Math.Min((double)bitmap.Width / frame.Width, (double)bitmap.Height / frame.Height);
                int width = (int)(frame.Width * scale);
                int height = (int)(frame.Height * scale);
                graphics.DrawImage(frame, (bitmap.Width - width) / 2, (bitmap.Height - height) / 2, width, height);
            }

            var bounds = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var locked = bitmap.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                for (int row = 0; row < bitmap.Height; row++)
                    Marshal.Copy(locked.Scan0 + row * locked.Stride, pixels, row * bitmap.Width * 4, bitmap.Width * 4);
            }
            finally { bitmap.UnlockBits(locked); }
        }
        catch (Exception error) when (error is IOException or SocketException or AggregateException or ArgumentException)
        {
            Array.Clear(pixels);
        }
    }
}


// SPDX-License-Identifier: MIT
// Copyright (c) 2026 mugicha_xtx
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32.SafeHandles;

[assembly: AssemblyTitle("Biwamasu-Mixer Setting tool")]
[assembly: AssemblyDescription("Biwamasu Mixer 内部設定ツール")]
[assembly: AssemblyCompany("mugicha_xtx")]
[assembly: AssemblyProduct("Biwamasu Mixer")]
[assembly: AssemblyCopyright("Copyright (c) 2026 mugicha_xtx (MIT License)")]
[assembly: AssemblyVersion("1.4.0.0")]
[assembly: AssemblyFileVersion("1.4.0.0")]

namespace BiwamasuMixerGui
{
    internal static class UiScale
    {
        private static float _factor = 0f;

        public static float Factor
        {
            get
            {
                if (_factor <= 0f)
                {
                    try
                    {
                        using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                        {
                            _factor = g.DpiX / 96.0f;
                        }
                    }
                    catch
                    {
                        _factor = 1.0f;
                    }
                    if (_factor < 1.0f) _factor = 1.0f;
                }
                return _factor;
            }
        }

        public static int Px(int px96)
        {
            return (int)Math.Round(px96 * Factor);
        }

        public static float Pxf(float px96)
        {
            return (float)Math.Round(px96 * Factor);
        }

        public static Padding Pad(int l, int t, int r, int b)
        {
            return new Padding(Px(l), Px(t), Px(r), Px(b));
        }

        public static Font CreateUiFont()
        {
            try { return new Font("Yu Gothic UI", 9.0f, FontStyle.Regular, GraphicsUnit.Point); }
            catch { return SystemFonts.MessageBoxFont; }
        }

        public static Font CreateSmallFont()
        {
            try { return new Font("Yu Gothic UI", 7.8f, FontStyle.Regular, GraphicsUnit.Point); }
            catch { return SystemFonts.CaptionFont; }
        }

        public static Button CreateButton(string text, int width96, int height96)
        {
            Button b = new Button();
            b.Text = text;
            b.Width = Px(width96);
            b.Height = Px(height96);
            b.UseVisualStyleBackColor = true;
            b.Margin = Pad(0, 2, 6, 2);
            return b;
        }
    }

    internal static class AppIconFactory
    {
        private delegate void SvgShader(double x, double y, int idx, out double r, out double g, out double b);

        private static double RadialT(double x, double y, double tx, double ty, double deg, double sx, double sy)
        {
            double rad = deg * (Math.PI / 180.0);
            double cs = Math.Cos(rad);
            double sn = Math.Sin(rad);
            double dx = x - tx;
            double dy = y - ty;
            double rx = (cs * dx + sn * dy) / sx;
            double ry = (-sn * dx + cs * dy) / sy;
            double t = Math.Sqrt(rx * rx + ry * ry);
            if (t < 0.0) return 0.0;
            if (t > 1.0) return 1.0;
            return t;
        }

        private static double LinearT(double x, double y, double x1, double y1, double x2, double y2)
        {
            double vx = x2 - x1;
            double vy = y2 - y1;
            double denom = vx * vx + vy * vy;
            if (denom <= 1e-12) return 0.0;
            double t = ((x - x1) * vx + (y - y1) * vy) / denom;
            if (t < 0.0) return 0.0;
            if (t > 1.0) return 1.0;
            return t;
        }

        private static double Ramp(double t, double t0, double t1)
        {
            if (t <= t0) return 0.0;
            if (t >= t1) return 1.0;
            return (t - t0) / (t1 - t0);
        }

        private static void LerpHex(int hex0, int hex1, double t, out double r, out double g, out double b)
        {
            double r0 = ((hex0 >> 16) & 0xFF) / 255.0;
            double g0 = ((hex0 >> 8) & 0xFF) / 255.0;
            double b0 = (hex0 & 0xFF) / 255.0;
            double r1 = ((hex1 >> 16) & 0xFF) / 255.0;
            double g1 = ((hex1 >> 8) & 0xFF) / 255.0;
            double b1 = (hex1 & 0xFF) / 255.0;
            r = r0 + (r1 - r0) * t;
            g = g0 + (g1 - g0) * t;
            b = b0 + (b1 - b0) * t;
        }

        private static void BlendHexOver(ref double r, ref double g, ref double b, int hexRgb, double alpha)
        {
            if (alpha <= 0.0) return;
            if (alpha > 1.0) alpha = 1.0;
            double sr = ((hexRgb >> 16) & 0xFF) / 255.0;
            double sg = ((hexRgb >> 8) & 0xFF) / 255.0;
            double sb = (hexRgb & 0xFF) / 255.0;
            double inv = 1.0 - alpha;
            r = sr * alpha + r * inv;
            g = sg * alpha + g * inv;
            b = sb * alpha + b * inv;
        }

        private static double CapsuleOutsideBlur(double x, double y, double sigma)
        {
            double cx = x < 11.65625 ? 11.65625 : (x > 16.34375 ? 16.34375 : x);
            double dx = x - cx;
            double dy = y - 19.34375;
            double s = (Math.Sqrt(dx * dx + dy * dy) - 8.65625) / sigma;
            if (s <= -5.0) return 0.0;
            if (s >= 5.0) return 1.0;
            return 0.5 * (1.0 + Math.Tanh(0.79788456 * s));
        }

        private static void RasterizePathAlpha(Bitmap maskBmp, byte[] scratchBgra, byte[] alphaOut, GraphicsPath path32, float scale)
        {
            using (GraphicsPath scaled = (GraphicsPath)path32.Clone())
            using (Matrix m = new Matrix(scale, 0f, 0f, scale, 0f, 0f))
            using (Graphics gr = Graphics.FromImage(maskBmp))
            {
                scaled.Transform(m);
                gr.Clear(Color.Transparent);
                gr.SmoothingMode = SmoothingMode.HighQuality;
                gr.PixelOffsetMode = PixelOffsetMode.HighQuality;
                gr.FillPath(Brushes.White, scaled);
            }

            BitmapData bd = maskBmp.LockBits(
                new Rectangle(0, 0, maskBmp.Width, maskBmp.Height),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppArgb);
            try
            {
                Marshal.Copy(bd.Scan0, scratchBgra, 0, scratchBgra.Length);
            }
            finally
            {
                maskBmp.UnlockBits(bd);
            }

            for (int i = 0, p = 3; i < alphaOut.Length; i++, p += 4)
            {
                alphaOut[i] = scratchBgra[p];
            }
        }

        private static void BlurAlphaRegion(
            byte[] src, byte[] tmp, byte[] dst,
            int w, int h, int x0, int y0, int x1, int y1, int radius)
        {
            Array.Clear(tmp, 0, tmp.Length);
            Array.Clear(dst, 0, dst.Length);
            int diam = radius * 2 + 1;

            for (int y = y0; y <= y1; y++)
            {
                int row = y * w;
                for (int x = x0; x <= x1; x++)
                {
                    int sum = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int sx = x + k;
                        if (sx < 0) sx = 0; else if (sx >= w) sx = w - 1;
                        sum += src[row + sx];
                    }
                    tmp[row + x] = (byte)((sum + diam / 2) / diam);
                }
            }

            for (int y = y0; y <= y1; y++)
            {
                int row = y * w;
                for (int x = x0; x <= x1; x++)
                {
                    int sum = 0;
                    for (int k = -radius; k <= radius; k++)
                    {
                        int sy = y + k;
                        if (sy < 0) sy = 0; else if (sy >= h) sy = h - 1;
                        sum += tmp[sy * w + x];
                    }
                    dst[row + x] = (byte)((sum + diam / 2) / diam);
                }
            }
        }

        private static void CompositeLayer(
            float[] accR, float[] accG, float[] accB, float[] accA,
            byte[] alphaMask, int w, int h, double invScale, double opacityScale, SvgShader shader)
        {
            for (int py = 0; py < h; py++)
            {
                double uy = (py + 0.5) * invScale;
                int row = py * w;
                for (int px = 0; px < w; px++)
                {
                    int idx = row + px;
                    byte ma = alphaMask[idx];
                    if (ma == 0) continue;
                    double sa = (ma / 255.0) * opacityScale;
                    if (sa <= 0.0) continue;
                    if (sa > 1.0) sa = 1.0;

                    double ux = (px + 0.5) * invScale;
                    double sr, sg, sb;
                    shader(ux, uy, idx, out sr, out sg, out sb);

                    float inv = (float)(1.0 - sa);
                    accR[idx] = (float)(sr * sa) + accR[idx] * inv;
                    accG[idx] = (float)(sg * sa) + accG[idx] * inv;
                    accB[idx] = (float)(sb * sa) + accB[idx] * inv;
                    accA[idx] = (float)sa + accA[idx] * inv;
                }
            }
        }

        // Renders Microsoft Fluent UI Emoji Fish (assets/Fish/Color/fish_color.svg, MIT License) directly from vector geometry.
        private static byte[] RenderFishEmojiPremul512(out int width, out int height)
        {
            const int S = 512;
            const float Scale = S / 32.0f;
            const double InvScale = 32.0 / S;
            width = S;
            height = S;

            int total = S * S;
            float[] accR = new float[total];
            float[] accG = new float[total];
            float[] accB = new float[total];
            float[] accA = new float[total];
            byte[] scratchBgra = new byte[total * 4];
            byte[] mask = new byte[total];
            byte[] blurTmp = new byte[total];
            byte[] blurA = new byte[total];
            byte[] blurB = new byte[total];

            using (Bitmap maskBmp = new Bitmap(S, S, PixelFormat.Format32bppArgb))
            {
                // 1. Lower mouth circle
                using (GraphicsPath p = new GraphicsPath())
                {
                    p.AddEllipse(3.05469f - 1.05469f, 19.9453f - 1.05469f, 2.10938f, 2.10938f);
                    RasterizePathAlpha(maskBmp, scratchBgra, mask, p, Scale);
                    CompositeLayer(accR, accG, accB, accA, mask, S, S, InvScale, 1.0, delegate(double x, double y, int idx, out double r, out double g, out double b)
                    {
                        LerpHex(0x3AAEE7, 0x45A5E8, RadialT(x, y, 3.21875, 19.6016, 150.684, 1.1648, 1.5939), out r, out g, out b);
                    });
                }

                // 2. Upper mouth circle
                using (GraphicsPath p = new GraphicsPath())
                {
                    p.AddEllipse(3.05469f - 1.05469f, 17.9453f - 1.05469f, 2.10938f, 2.10938f);
                    RasterizePathAlpha(maskBmp, scratchBgra, mask, p, Scale);
                    CompositeLayer(accR, accG, accB, accA, mask, S, S, InvScale, 1.0, delegate(double x, double y, int idx, out double r, out double g, out double b)
                    {
                        LerpHex(0x3AAEE7, 0x45A5E8, RadialT(x, y, 3.21875, 17.6016, 150.684, 1.1648, 1.5939), out r, out g, out b);
                    });
                }

                // 3. Body capsule (x=3, y=10.6875, w=22, h=17.3125, rx=8.65625)
                using (GraphicsPath p = new GraphicsPath())
                {
                    p.AddArc(3.0f, 10.6875f, 17.3125f, 17.3125f, 90.0f, 180.0f);
                    p.AddArc(25.0f - 17.3125f, 10.6875f, 17.3125f, 17.3125f, 270.0f, 180.0f);
                    p.CloseFigure();
                    RasterizePathAlpha(maskBmp, scratchBgra, mask, p, Scale);
                    CompositeLayer(accR, accG, accB, accA, mask, S, S, InvScale, 1.0, delegate(double x, double y, int idx, out double r, out double g, out double b)
                    {
                        LerpHex(0x50CAFF, 0x4D7FFF, RadialT(x, y, 11.0, 16.0, 52.125, 18.5279, 24.3438), out r, out g, out b);
                        BlendHexOver(ref r, ref g, ref b, 0x508BFF, 1.0 - LinearT(x, y, 14.0, 29.375, 14.0, 24.4375));
                        BlendHexOver(ref r, ref g, ref b, 0x5D68FF, CapsuleOutsideBlur(x + 0.5, y + 0.5, 1.0));
                        BlendHexOver(ref r, ref g, ref b, 0x2A69D2, CapsuleOutsideBlur(x + 0.25, y, 0.25));
                        BlendHexOver(ref r, ref g, ref b, 0x50CAFF, 1.0 - RadialT(x, y, 5.75, 11.5, 54.3004, 12.8527, 14.7095));
                        BlendHexOver(ref r, ref g, ref b, 0x59C0E7, Ramp(RadialT(x, y, 26.0, 29.1875, -144.29, 26.5554, 22.4157), 0.935277, 1.0));
                    });
                }

                // 4. Gill soft glow + crisp gill crescent
                using (GraphicsPath p = new GraphicsPath())
                {
                    p.StartFigure();
                    p.AddBezier(12.4303f, 14.1246f, 12.2187f, 13.9472f, 11.9033f, 13.9750f, 11.7260f, 14.1867f);
                    p.AddBezier(11.7260f, 14.1867f, 11.5486f, 14.3983f, 11.5764f, 14.7137f, 11.7880f, 14.8910f);
                    p.AddBezier(11.7880f, 14.8910f, 14.3327f, 17.0234f, 14.5775f, 20.8423f, 11.7459f, 23.8361f);
                    p.AddBezier(11.7459f, 23.8361f, 11.5562f, 24.0367f, 11.5650f, 24.3532f, 11.7656f, 24.5429f);
                    p.AddBezier(11.7656f, 24.5429f, 11.9662f, 24.7327f, 12.2827f, 24.7239f, 12.4725f, 24.5233f);
                    p.AddBezier(12.4725f, 24.5233f, 15.6409f, 21.1733f, 15.4794f, 16.6797f, 12.4303f, 14.1246f);
                    p.CloseFigure();
                    RasterizePathAlpha(maskBmp, scratchBgra, mask, p, Scale);

                    // Soft glow (stdDeviation=1 => ~16px; 2-pass box blur radius 14)
                    BlurAlphaRegion(mask, blurTmp, blurA, S, S, 136, 176, 296, 444, 14);
                    BlurAlphaRegion(blurA, blurTmp, blurB, S, S, 136, 176, 296, 444, 14);
                    CompositeLayer(accR, accG, accB, accA, blurB, S, S, InvScale, 1.0, delegate(double x, double y, int idx, out double r, out double g, out double b)
                    {
                        LerpHex(0x3A94E7, 0x3A94E7, 0.0, out r, out g, out b);
                    });

                    // Inner shadow blur (stdDeviation=0.3 => ~5px)
                    BlurAlphaRegion(mask, blurTmp, blurA, S, S, 176, 214, 258, 406, 5);
                    CompositeLayer(accR, accG, accB, accA, mask, S, S, InvScale, 1.0, delegate(double x, double y, int idx, out double r, out double g, out double b)
                    {
                        LerpHex(0x3F61E7, 0x3F61E7, 0.0, out r, out g, out b);
                        BlendHexOver(ref r, ref g, ref b, 0x4A8CFF, 1.0 - Ramp(RadialT(x, y, 11.9062, 24.5078, -51.9939, 1.49717, 0.843924), 0.0, 0.609165));
                        BlendHexOver(ref r, ref g, ref b, 0x4A8CFF, 1.0 - Ramp(RadialT(x, y, 12.0469, 14.4297, 46.7357, 1.27669, 0.760996), 0.0, 0.609165));
                        BlendHexOver(ref r, ref g, ref b, 0x4F7FE6, 1.0 - Ramp(RadialT(x, y, 13.1964, 19.3437, 0.0, 1.64731, 3.34375), 0.203247, 1.0));
                        BlendHexOver(ref r, ref g, ref b, 0x2270F0, 1.0 - (blurA[idx] / 255.0));
                    });
                }

                // 5. Eye circle
                using (GraphicsPath p = new GraphicsPath())
                {
                    p.AddEllipse(8.67969f - 0.5f, 17.4805f - 0.5f, 1.0f, 1.0f);
                    RasterizePathAlpha(maskBmp, scratchBgra, mask, p, Scale);
                    CompositeLayer(accR, accG, accB, accA, mask, S, S, InvScale, 1.0, delegate(double x, double y, int idx, out double r, out double g, out double b)
                    {
                        LerpHex(0x433437, 0x3B2838, Ramp(RadialT(x, y, 8.82813, 17.3196, 125.838, 0.693863, 0.618606), 0.00558659, 1.0), out r, out g, out b);
                        BlendHexOver(ref r, ref g, ref b, 0x5C5051, 1.0 - RadialT(x, y, 8.86655, 17.3555, 137.643, 0.35946, 0.316238));
                        BlendHexOver(ref r, ref g, ref b, 0x5C5051, LinearT(x, y, 8.58139, 17.0877, 8.23764, 16.869));
                    });
                }

                // 6. Lower tail fin
                using (GraphicsPath p = new GraphicsPath())
                {
                    p.StartFigure();
                    p.AddBezier(25.0f, 24.125f, 25.0f, 26.3341f, 26.7909f, 28.125f, 29.0f, 28.125f);
                    p.AddLine(29.0f, 28.125f, 29.2188f, 28.125f);
                    p.AddBezier(29.2188f, 28.125f, 29.4949f, 28.125f, 29.7188f, 27.9011f, 29.7188f, 27.625f);
                    p.AddLine(29.7188f, 27.625f, 29.7188f, 23.375f);
                    p.AddBezier(29.7188f, 23.375f, 29.7188f, 21.1659f, 27.9279f, 19.375f, 25.7188f, 19.375f);
                    p.AddLine(25.7188f, 19.375f, 25.0f, 19.375f);
                    p.CloseFigure();
                    RasterizePathAlpha(maskBmp, scratchBgra, mask, p, Scale);
                    CompositeLayer(accR, accG, accB, accA, mask, S, S, InvScale, 1.0, delegate(double x, double y, int idx, out double r, out double g, out double b)
                    {
                        LerpHex(0x46AAFF, 0x368AFF, RadialT(x, y, 28.375, 20.75, 89.029, 7.37606, 7.47018), out r, out g, out b);
                        BlendHexOver(ref r, ref g, ref b, 0x3184E6, Ramp(RadialT(x, y, 32.8125, 20.6875, 135.266, 9.50185, 15.6131), 0.800545, 1.0));
                        BlendHexOver(ref r, ref g, ref b, 0x3184E6, Ramp(LinearT(x, y, 38.4375, 25.5625, 25.0, 25.5625), 0.848889, 1.0));
                        BlendHexOver(ref r, ref g, ref b, 0x54BDFF, 1.0 - Ramp(RadialT(x, y, 29.7187, 21.125, 90.0, 7.0, 2.96293), 0.0, 0.92268));
                    });
                }

                // 7. Upper tail fin
                using (GraphicsPath p = new GraphicsPath())
                {
                    p.StartFigure();
                    p.AddBezier(25.0f, 14.625f, 25.0f, 12.4159f, 26.7909f, 10.625f, 29.0f, 10.625f);
                    p.AddLine(29.0f, 10.625f, 29.2188f, 10.625f);
                    p.AddBezier(29.2188f, 10.625f, 29.4949f, 10.625f, 29.7188f, 10.8489f, 29.7188f, 11.125f);
                    p.AddLine(29.7188f, 11.125f, 29.7188f, 15.375f);
                    p.AddBezier(29.7188f, 15.375f, 29.7188f, 17.5841f, 27.9279f, 19.375f, 25.7188f, 19.375f);
                    p.AddLine(25.7188f, 19.375f, 25.0f, 19.375f);
                    p.CloseFigure();
                    RasterizePathAlpha(maskBmp, scratchBgra, mask, p, Scale);
                    CompositeLayer(accR, accG, accB, accA, mask, S, S, InvScale, 1.0, delegate(double x, double y, int idx, out double r, out double g, out double b)
                    {
                        LerpHex(0x46AAFF, 0x368AFF, RadialT(x, y, 28.7261, 11.5895, 109.396, 8.58528, 6.27348), out r, out g, out b);
                        BlendHexOver(ref r, ref g, ref b, 0x3184E6, Ramp(RadialT(x, y, 33.1875, 16.0, -165.196, 9.05042, 14.8713), 0.725568, 1.0));
                        BlendHexOver(ref r, ref g, ref b, 0x54BDFF, 1.0 - Ramp(RadialT(x, y, 29.7188, 11.0937, 90.0, 6.3125, 1.16539), 0.0, 0.92268));
                    });
                }

                // 8. Ventral (bottom) fin
                using (GraphicsPath p = new GraphicsPath())
                {
                    p.StartFigure();
                    p.AddBezier(15.1708f, 28.0f, 16.1466f, 28.0f, 16.9377f, 28.7910f, 16.9377f, 29.7668f);
                    p.AddBezier(16.9377f, 29.7668f, 16.9377f, 29.8888f, 16.8388f, 29.9877f, 16.7168f, 29.9877f);
                    p.AddLine(16.7168f, 29.9877f, 14.9563f, 29.9877f);
                    p.AddBezier(14.9563f, 29.9877f, 13.8585f, 29.9877f, 12.9686f, 29.0978f, 12.9686f, 28.0f);
                    p.CloseFigure();
                    RasterizePathAlpha(maskBmp, scratchBgra, mask, p, Scale);
                    CompositeLayer(accR, accG, accB, accA, mask, S, S, InvScale, 1.0, delegate(double x, double y, int idx, out double r, out double g, out double b)
                    {
                        LerpHex(0x4286FF, 0x3170FF, RadialT(x, y, 15.6875, 29.5, -156.615, 2.51946, 1.78774), out r, out g, out b);
                        BlendHexOver(ref r, ref g, ref b, 0x0E66FD, 1.0 - LinearT(x, y, 15.1875, 27.5312, 15.2188, 28.5156));
                        BlendHexOver(ref r, ref g, ref b, 0x519DFF, 1.0 - Ramp(RadialT(x, y, 17.2344, 29.8125, -153.997, 2.85112, 1.12711), 0.0, 0.969412));
                    });
                }

                // 9. Dorsal (top) fin
                using (GraphicsPath p = new GraphicsPath())
                {
                    p.StartFigure();
                    p.AddBezier(17.7502f, 10.6875f, 19.9593f, 10.6875f, 21.7502f, 8.89664f, 21.7502f, 6.6875f);
                    p.AddLine(21.7502f, 6.6875f, 21.7502f, 6.35937f);
                    p.AddBezier(21.7502f, 6.35937f, 21.7502f, 6.08323f, 21.5263f, 5.85937f, 21.2502f, 5.85937f);
                    p.AddLine(21.2502f, 5.85937f, 16.1092f, 5.85937f);
                    p.AddBezier(16.1092f, 5.85937f, 13.9001f, 5.85937f, 12.1092f, 7.65024f, 12.1092f, 9.85937f);
                    p.AddLine(12.1092f, 9.85937f, 12.1092f, 10.6875f);
                    p.CloseFigure();
                    RasterizePathAlpha(maskBmp, scratchBgra, mask, p, Scale);
                    CompositeLayer(accR, accG, accB, accA, mask, S, S, InvScale, 1.0, delegate(double x, double y, int idx, out double r, out double g, out double b)
                    {
                        LerpHex(0x46AAFF, 0x2B86D9, RadialT(x, y, 20.6875, 6.875, 156.038, 9.38736, 4.65552), out r, out g, out b);
                        BlendHexOver(ref r, ref g, ref b, 0x0A75CC, 1.0 - Ramp(LinearT(x, y, 12.1092, 11.4687, 12.1092, 9.46875), 0.109375, 1.0));
                        BlendHexOver(ref r, ref g, ref b, 0x54BDFF, 1.0 - Ramp(RadialT(x, y, 22.25, 5.5, 143.267, 5.22502, 1.64552), 0.0, 0.644093));
                    });
                }
            }

            byte[] outPremul = new byte[total * 4];
            for (int i = 0; i < total; i++)
            {
                int b = (int)Math.Round(accB[i] * 255.0f);
                int g = (int)Math.Round(accG[i] * 255.0f);
                int r = (int)Math.Round(accR[i] * 255.0f);
                int a = (int)Math.Round(accA[i] * 255.0f);
                if (a <= 0) continue;
                if (a > 255) a = 255;
                if (b < 0) b = 0; else if (b > a) b = a;
                if (g < 0) g = 0; else if (g > a) g = a;
                if (r < 0) r = 0; else if (r > a) r = a;
                int p = i * 4;
                outPremul[p] = (byte)b;
                outPremul[p + 1] = (byte)g;
                outPremul[p + 2] = (byte)r;
                outPremul[p + 3] = (byte)a;
            }
            return outPremul;
        }

        private static byte[] ResampleCenteredStraightBgra(
            byte[] srcPremul, int srcW, int srcH,
            int minX, int minY, int maxX, int maxY,
            int dstSize)
        {
            int inkW = maxX - minX + 1;
            int inkH = maxY - minY + 1;
            double inkMax = Math.Max(inkW, inkH);
            double fillRatio = dstSize <= 24 ? 0.96 : (dstSize <= 48 ? 0.94 : 0.92);
            double srcSpan = inkMax / fillRatio;
            double cx = (minX + maxX + 1) * 0.5;
            double cy = (minY + maxY + 1) * 0.5;
            double srcX0 = cx - srcSpan * 0.5;
            double srcY0 = cy - srcSpan * 0.5;
            double scale = srcSpan / dstSize;
            double totalBoxArea = scale * scale;

            byte[] dst = new byte[dstSize * dstSize * 4];
            for (int dy = 0; dy < dstSize; dy++)
            {
                double syStart = srcY0 + dy * scale;
                double syEnd = syStart + scale;
                int iy0 = Math.Max(0, (int)Math.Floor(syStart));
                int iy1 = Math.Min(srcH - 1, (int)Math.Ceiling(syEnd) - 1);

                for (int dx = 0; dx < dstSize; dx++)
                {
                    double sxStart = srcX0 + dx * scale;
                    double sxEnd = sxStart + scale;
                    int ix0 = Math.Max(0, (int)Math.Floor(sxStart));
                    int ix1 = Math.Min(srcW - 1, (int)Math.Ceiling(sxEnd) - 1);

                    double sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                    for (int sy = iy0; sy <= iy1; sy++)
                    {
                        double wy = Math.Min(sy + 1.0, syEnd) - Math.Max((double)sy, syStart);
                        if (wy <= 0) continue;
                        int rowOff = sy * srcW * 4;
                        for (int sx = ix0; sx <= ix1; sx++)
                        {
                            double wx = Math.Min(sx + 1.0, sxEnd) - Math.Max((double)sx, sxStart);
                            if (wx <= 0) continue;
                            double w = wx * wy;
                            int p = rowOff + sx * 4;
                            sumB += srcPremul[p] * w;
                            sumG += srcPremul[p + 1] * w;
                            sumR += srcPremul[p + 2] * w;
                            sumA += srcPremul[p + 3] * w;
                        }
                    }

                    double pb = sumB / totalBoxArea;
                    double pg = sumG / totalBoxArea;
                    double pr = sumR / totalBoxArea;
                    double pa = sumA / totalBoxArea;

                    int a = (int)Math.Round(pa);
                    if (a <= 0) continue;
                    if (a > 255) a = 255;

                    int b = (int)Math.Round(pb * 255.0 / pa);
                    int g = (int)Math.Round(pg * 255.0 / pa);
                    int r = (int)Math.Round(pr * 255.0 / pa);
                    if (b < 0) b = 0; else if (b > 255) b = 255;
                    if (g < 0) g = 0; else if (g > 255) g = 255;
                    if (r < 0) r = 0; else if (r > 255) r = 255;

                    int dp = (dy * dstSize + dx) * 4;
                    dst[dp] = (byte)b;
                    dst[dp + 1] = (byte)g;
                    dst[dp + 2] = (byte)r;
                    dst[dp + 3] = (byte)a;
                }
            }
            return dst;
        }

        private static byte[] EncodeDibIconFrame(byte[] bgraTopDown, int size)
        {
            int xorBytes = size * size * 4;
            int andRowBytes = ((size + 31) / 32) * 4;
            int andBytes = andRowBytes * size;

            using (MemoryStream ms = new MemoryStream(40 + xorBytes + andBytes))
            using (BinaryWriter bw = new BinaryWriter(ms))
            {
                bw.Write(40u);
                bw.Write(size);
                bw.Write(size * 2);
                bw.Write((ushort)1);
                bw.Write((ushort)32);
                bw.Write(0u);
                bw.Write((uint)(xorBytes + andBytes));
                bw.Write(0);
                bw.Write(0);
                bw.Write(0u);
                bw.Write(0u);

                for (int y = size - 1; y >= 0; y--)
                {
                    bw.Write(bgraTopDown, y * size * 4, size * 4);
                }

                byte[] maskRow = new byte[andRowBytes];
                for (int y = size - 1; y >= 0; y--)
                {
                    Array.Clear(maskRow, 0, maskRow.Length);
                    int rowOff = y * size * 4;
                    for (int x = 0; x < size; x++)
                    {
                        if (bgraTopDown[rowOff + x * 4 + 3] == 0)
                        {
                            maskRow[x >> 3] |= (byte)(0x80 >> (x & 7));
                        }
                    }
                    bw.Write(maskRow);
                }
                return ms.ToArray();
            }
        }

        public static byte[] BuildFishEmojiIcoBytes()
        {
            int srcW, srcH;
            byte[] premul = RenderFishEmojiPremul512(out srcW, out srcH);

            int minX = srcW, minY = srcH, maxX = -1, maxY = -1;
            for (int y = 0; y < srcH; y++)
            {
                int row = y * srcW * 4;
                for (int x = 0; x < srcW; x++)
                {
                    if (premul[row + x * 4 + 3] >= 4)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }
            if (maxX < minX || maxY < minY)
            {
                throw new InvalidOperationException("Fish emoji glyph rendered empty.");
            }

            int[] sizes = new int[] { 16, 24, 32, 48, 64, 256 };
            List<byte[]> payloads = new List<byte[]>(sizes.Length);
            for (int i = 0; i < sizes.Length; i++)
            {
                int sz = sizes[i];
                byte[] bgra = ResampleCenteredStraightBgra(premul, srcW, srcH, minX, minY, maxX, maxY, sz);
                if (sz == 256)
                {
                    using (Bitmap bmp = new Bitmap(sz, sz, PixelFormat.Format32bppArgb))
                    using (MemoryStream pms = new MemoryStream())
                    {
                        BitmapData bd = bmp.LockBits(new Rectangle(0, 0, sz, sz), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                        Marshal.Copy(bgra, 0, bd.Scan0, bgra.Length);
                        bmp.UnlockBits(bd);
                        bmp.Save(pms, ImageFormat.Png);
                        payloads.Add(pms.ToArray());
                    }
                }
                else
                {
                    payloads.Add(EncodeDibIconFrame(bgra, sz));
                }
            }

            using (MemoryStream icoMs = new MemoryStream())
            using (BinaryWriter bw = new BinaryWriter(icoMs))
            {
                bw.Write((ushort)0);
                bw.Write((ushort)1);
                bw.Write((ushort)sizes.Length);
                uint offset = (uint)(6 + 16 * sizes.Length);
                for (int i = 0; i < sizes.Length; i++)
                {
                    int sz = sizes[i];
                    bw.Write((byte)(sz >= 256 ? 0 : sz));
                    bw.Write((byte)(sz >= 256 ? 0 : sz));
                    bw.Write((byte)0);
                    bw.Write((byte)0);
                    bw.Write((ushort)1);
                    bw.Write((ushort)32);
                    bw.Write((uint)payloads[i].Length);
                    bw.Write(offset);
                    offset += (uint)payloads[i].Length;
                }
                for (int i = 0; i < payloads.Count; i++)
                {
                    bw.Write(payloads[i]);
                }
                return icoMs.ToArray();
            }
        }

        public static Icon CreateAppIcon()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("BiwamasuMixerGui.fish.ico"))
                {
                    if (s != null)
                    {
                        return new Icon(s);
                    }
                }
            }
            catch { }

            try
            {
                byte[] icoBytes = BuildFishEmojiIcoBytes();
                using (MemoryStream ms = new MemoryStream(icoBytes))
                {
                    return new Icon(ms);
                }
            }
            catch { }

            try
            {
                return Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch
            {
                return null;
            }
        }
    }

    internal enum FilterKind
    {
        PK = 0,
        LSC = 1,
        HSC = 2
    }

    internal sealed class EqBandSpec
    {
        public bool Enabled;
        public FilterKind Kind;
        public double FrequencyHz;
        public double GainDb;
        public double Q;

        public EqBandSpec()
        {
            Enabled = false;
            Kind = FilterKind.PK;
            FrequencyHz = 1000.0;
            GainDb = 0.0;
            Q = 1.41;
        }

        public EqBandSpec Clone()
        {
            EqBandSpec copy = new EqBandSpec();
            copy.Enabled = Enabled;
            copy.Kind = Kind;
            copy.FrequencyHz = FrequencyHz;
            copy.GainDb = GainDb;
            copy.Q = Q;
            return copy;
        }
    }

    internal sealed class EqProfile
    {
        public double PreampDb;
        public string SourceLabel;
        public EqBandSpec[] Bands;

        public EqProfile()
        {
            PreampDb = 0.0;
            SourceLabel = "標準11バンド";
            Bands = new EqBandSpec[11];
            double[] defaultFreqs = new double[] { 100, 200, 350, 500, 1000, 2000, 3500, 5000, 7000, 8500, 10000 };
            for (int i = 0; i < 11; i++)
            {
                EqBandSpec b = new EqBandSpec();
                b.Enabled = true;
                b.Kind = (i == 0) ? FilterKind.LSC : ((i == 10) ? FilterKind.HSC : FilterKind.PK);
                b.FrequencyHz = defaultFreqs[i];
                b.GainDb = 0.0;
                b.Q = (i == 0 || i == 10) ? 0.71 : 1.41;
                Bands[i] = b;
            }
        }

        public static EqProfile CreateGraphic10BandTemplate()
        {
            EqProfile p = new EqProfile();
            p.PreampDb = 0.0;
            p.SourceLabel = "グラフィックEQ風";
            double[] freqs = new double[] { 31.5, 63, 125, 250, 500, 1000, 2000, 4000, 8000, 16000, 20000 };
            for (int i = 0; i < 11; i++)
            {
                EqBandSpec b = new EqBandSpec();
                b.Enabled = (i < 10);
                b.Kind = FilterKind.PK;
                b.FrequencyHz = freqs[i];
                b.GainDb = 0.0;
                b.Q = 1.41;
                p.Bands[i] = b;
            }
            return p;
        }

        public static EqProfile CreateBassBoostSample()
        {
            EqProfile p = new EqProfile();
            p.PreampDb = -4.0;
            p.SourceLabel = "低音ブースト";
            p.Bands[0].GainDb = 3.5;
            p.Bands[1].GainDb = -1.0;
            p.Bands[6].GainDb = 1.0;
            return p;
        }

        public static EqProfile CreateVoiceFootstepSample()
        {
            EqProfile p = new EqProfile();
            p.PreampDb = -3.5;
            p.SourceLabel = "ボイス・足音重視";
            p.Bands[0].GainDb = -3.0;
            p.Bands[4].GainDb = 2.0;
            p.Bands[5].GainDb = 3.0;
            p.Bands[6].GainDb = 2.5;
            p.Bands[10].GainDb = -1.5;
            return p;
        }

        public EqProfile Clone()
        {
            EqProfile copy = new EqProfile();
            copy.PreampDb = PreampDb;
            copy.SourceLabel = SourceLabel;
            for (int i = 0; i < 11; i++)
            {
                copy.Bands[i] = Bands[i].Clone();
            }
            return copy;
        }

        public int ActiveBandCount()
        {
            int n = 0;
            for (int i = 0; i < Bands.Length; i++)
            {
                if (Bands[i].Enabled) n++;
            }
            return n;
        }
    }

    internal static class EqDspMath
    {
        private static readonly Regex PreampRx = new Regex(
            @"^Preamp\s*:\s*([^\s]+)\s*dB\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FilterHeadRx = new Regex(
            @"^Filter\s*\d*\s*:\s*(ON|OFF)\s+([A-Za-z0-9]+)(?:\s+(.*))?$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex FilterBodyRx = new Regex(
            @"^Fc\s+([^\s]+)\s*Hz\s+Gain\s+([^\s]+)\s*dB(?:\s+(?:Q\s+([^\s]+)|BW\s+Oct\s+([^\s]+)))?\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex ThousandsCommaRx = new Regex(
            @"(?<=\d),(?=\d{3}(?!\d))",
            RegexOptions.Compiled);

        private static readonly Regex PresetCommentRx = new Regex(
            @"^\s*(?:#|//|;)\s*(?:Preset|Name|設定名)\s*[:：]\s*(.+?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SectionRx = new Regex(
            @"^\s*\[([^\]]+)\]\s*$",
            RegexOptions.Compiled);

        private static readonly Regex KeyValRx = new Regex(
            @"^\s*([A-Za-z]+)(\d*)\s*=\s*(.+?)\s*$",
            RegexOptions.Compiled);

        public static bool IsFinite(double v)
        {
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }

        public static bool TryParseStrictNumber(string raw, out double value)
        {
            value = 0.0;
            if (string.IsNullOrEmpty(raw)) return false;
            string s = NormalizeNumericToken(raw);
            if (s.IndexOf(',') >= 0)
            {
                string withoutThousands = ThousandsCommaRx.Replace(s, "");
                if (withoutThousands.IndexOf(',') >= 0)
                {
                    return false;
                }
                s = withoutThousands;
            }
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            {
                return false;
            }
            return IsFinite(value);
        }

        private static string NormalizeNumericToken(string raw)
        {
            StringBuilder sb = new StringBuilder(raw.Length);
            for (int i = 0; i < raw.Length; i++)
            {
                char ch = raw[i];
                if (ch >= '\uFF01' && ch <= '\uFF5E')
                {
                    ch = (char)(ch - 0xFEE0);
                }
                if (ch == '\u2212' || ch == '\u2013' || ch == '\u2014' || ch == '\u2010' || ch == '\u2011' || ch == '\uFE63')
                {
                    ch = '-';
                }
                else if (ch == '\u3000')
                {
                    ch = ' ';
                }
                sb.Append(ch);
            }
            return sb.ToString().Trim();
        }

        private static string NormalizeEqDirectiveLine(string raw)
        {
            string s = NormalizeNumericToken(raw);
            if (s.Length == 0) return s;

            // Strip inline code backticks and Markdown list/quote prefixes
            s = s.Trim('`').Trim();
            while (s.Length > 0)
            {
                if (s.StartsWith("- ") || s.StartsWith("* ") || s.StartsWith("> "))
                {
                    s = s.Substring(2).TrimStart();
                }
                else if (s.StartsWith("・"))
                {
                    s = s.Substring(1).TrimStart();
                }
                else
                {
                    break;
                }
            }

            // Strip Markdown bold/italic emphasis (**Preamp:**, *Filter 1:*, __Preamp__)
            s = s.Replace("**", "").Replace("__", "").Replace("*", "").Trim();

            // Strip trailing inline comments (# or ;) if present after settings
            int hashIdx = s.IndexOf(" #", StringComparison.Ordinal);
            if (hashIdx > 0) s = s.Substring(0, hashIdx).TrimEnd();
            int semiIdx = s.IndexOf(" ;", StringComparison.Ordinal);
            if (semiIdx > 0) s = s.Substring(0, semiIdx).TrimEnd();

            return s;
        }

        public static double BandwidthOctavesToQ(double bwOct)
        {
            if (!IsFinite(bwOct) || bwOct <= 0.02 || bwOct > 20.0) return -1.0;
            double pow2 = Math.Pow(2.0, bwOct);
            double denom = pow2 - 1.0;
            if (denom <= 1e-9) return -1.0;
            return Math.Sqrt(pow2) / denom;
        }

        public static float ComputePreampLinear(double preampDb, out string error)
        {
            error = null;
            if (!IsFinite(preampDb) || preampDb < -36.0 || preampDb > 12.0)
            {
                error = "プリアンプは -36.0 〜 +12.0 dB の範囲で指定してください。";
                return 1.0f;
            }
            float lin = (float)Math.Pow(10.0, preampDb / 20.0);
            if (!FloatSane(lin) || !(lin >= 0.01f && lin <= 4.0f))
            {
                error = "プリアンプの値が範囲外です (-36.0 〜 +12.0 dB)。";
                return 1.0f;
            }
            return lin;
        }

        public static float[] ComputeBiquad(EqBandSpec band, int bandNumberForError, out string error)
        {
            error = null;
            if (!band.Enabled)
            {
                return new float[] { 1.0f, 0.0f, 0.0f, 0.0f, 0.0f };
            }

            if (!IsFinite(band.FrequencyHz) || band.FrequencyHz < 10.0 || band.FrequencyHz > 23500.0)
            {
                error = string.Format("Band {0}: 周波数は 10 〜 23500 Hz の範囲で指定してください。", bandNumberForError);
                return null;
            }
            if (!IsFinite(band.Q) || band.Q < 0.06 || band.Q > 30.0)
            {
                error = string.Format("Band {0}: Q値は 0.06 〜 30.0 の範囲で指定してください。", bandNumberForError);
                return null;
            }
            if (!IsFinite(band.GainDb) || band.GainDb < -30.0 || band.GainDb > 24.0)
            {
                error = string.Format("Band {0}: ゲインは -30.0 〜 +24.0 dB の範囲で指定してください。", bandNumberForError);
                return null;
            }

            const double fs = 48000.0;
            double a = Math.Pow(10.0, band.GainDb / 40.0);
            double w0 = 2.0 * Math.PI * band.FrequencyHz / fs;
            double cw = Math.Cos(w0);
            double sw = Math.Sin(w0);
            double alpha = sw / (2.0 * band.Q);

            double b0, b1, b2, a0, a1, a2;
            if (band.Kind == FilterKind.PK)
            {
                b0 = 1.0 + alpha * a;
                b1 = -2.0 * cw;
                b2 = 1.0 - alpha * a;
                a0 = 1.0 + alpha / a;
                a1 = -2.0 * cw;
                a2 = 1.0 - alpha / a;
            }
            else if (band.Kind == FilterKind.LSC)
            {
                double sa = 2.0 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1.0) - (a - 1.0) * cw + sa);
                b1 = 2.0 * a * ((a - 1.0) - (a + 1.0) * cw);
                b2 = a * ((a + 1.0) - (a - 1.0) * cw - sa);
                a0 = (a + 1.0) + (a - 1.0) * cw + sa;
                a1 = -2.0 * ((a - 1.0) + (a + 1.0) * cw);
                a2 = (a + 1.0) + (a - 1.0) * cw - sa;
            }
            else
            {
                double sa = 2.0 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1.0) + (a - 1.0) * cw + sa);
                b1 = -2.0 * a * ((a - 1.0) + (a + 1.0) * cw);
                b2 = a * ((a + 1.0) + (a - 1.0) * cw - sa);
                a0 = (a + 1.0) - (a - 1.0) * cw + sa;
                a1 = 2.0 * ((a - 1.0) - (a + 1.0) * cw);
                a2 = (a + 1.0) - (a - 1.0) * cw - sa;
            }

            float nb0 = (float)(b0 / a0);
            float nb1 = (float)(b1 / a0);
            float nb2 = (float)(b2 / a0);
            float na1 = (float)(a1 / a0);
            float na2 = (float)(a2 / a0);

            if (!FloatSane(nb0) || !FloatSane(nb1) || !FloatSane(nb2) || !FloatSane(na1) || !FloatSane(na2) ||
                Math.Abs(na2) >= 1.0f || (1.0f + na2) <= Math.Abs(na1))
            {
                error = string.Format("Band {0}: フィルター係数が安定範囲を超えました。ゲインまたはQ値を控えめにしてください。", bandNumberForError);
                return null;
            }

            return new float[] { nb0, nb1, nb2, na1, na2 };
        }

        public static double EvaluateCascadeDbAtHz(float[][] coeffs, double preampDb, double freqHz)
        {
            const double fs = 48000.0;
            double w = 2.0 * Math.PI * freqHz / fs;
            double c1 = Math.Cos(w);
            double s1 = Math.Sin(w);
            double c2 = 2.0 * c1 * c1 - 1.0;
            double s2 = 2.0 * s1 * c1;

            double totalDb = preampDb;
            for (int i = 0; i < coeffs.Length; i++)
            {
                float[] c = coeffs[i];
                if (c == null) continue;
                double b0 = c[0], b1 = c[1], b2 = c[2], a1 = c[3], a2 = c[4];
                if (b0 == 1.0 && b1 == 0.0 && b2 == 0.0 && a1 == 0.0 && a2 == 0.0) continue;

                double numRe = b0 + b1 * c1 + b2 * c2;
                double numIm = -(b1 * s1 + b2 * s2);
                double denRe = 1.0 + a1 * c1 + a2 * c2;
                double denIm = -(a1 * s1 + a2 * s2);

                double numSq = numRe * numRe + numIm * numIm;
                double denSq = denRe * denRe + denIm * denIm;
                if (denSq > 1e-18 && numSq > 1e-18)
                {
                    totalDb += 10.0 * Math.Log10(numSq / denSq);
                }
            }
            return totalDb;
        }

        public static double ComputePeakFilterBoostDb(EqProfile profile, out double peakWithPreampDb)
        {
            float[][] coeffs = new float[11][];
            string err;
            for (int i = 0; i < 11; i++)
            {
                coeffs[i] = ComputeBiquad(profile.Bands[i], i + 1, out err);
                if (coeffs[i] == null) coeffs[i] = new float[] { 1f, 0f, 0f, 0f, 0f };
            }

            double maxRaw = -120.0;
            const int steps = 240;
            double logMin = Math.Log10(20.0);
            double logMax = Math.Log10(20000.0);
            for (int k = 0; k <= steps; k++)
            {
                double f = Math.Pow(10.0, logMin + (logMax - logMin) * k / steps);
                double db = EvaluateCascadeDbAtHz(coeffs, 0.0, f);
                if (db > maxRaw) maxRaw = db;
            }
            for (int i = 0; i < 11; i++)
            {
                if (profile.Bands[i].Enabled)
                {
                    double f = Clamp(profile.Bands[i].FrequencyHz, 20.0, 20000.0);
                    double db = EvaluateCascadeDbAtHz(coeffs, 0.0, f);
                    if (db > maxRaw) maxRaw = db;
                }
            }
            if (maxRaw < -60.0) maxRaw = 0.0;
            peakWithPreampDb = maxRaw + profile.PreampDb;
            return maxRaw;
        }

        public static string FormatFrequencyClean(double hz)
        {
            if (Math.Abs(hz - Math.Round(hz)) < 0.05)
            {
                return string.Format(CultureInfo.InvariantCulture, "{0:0}", Math.Round(hz));
            }
            return string.Format(CultureInfo.InvariantCulture, "{0:0.0}", hz);
        }

        private static bool FloatSane(float x)
        {
            return !float.IsNaN(x) && !float.IsInfinity(x) && x > -20.0f && x < 20.0f;
        }

        public static EqProfile ParsePeaceFile(string path)
        {
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("指定されたファイルが見つかりません: " + path);
            }

            string raw = File.ReadAllText(path, Encoding.UTF8);
            bool forceIni = path.EndsWith(".peace", StringComparison.OrdinalIgnoreCase);
            return ParsePeaceLinesCore(
                raw.Split(new string[] { "\r\n", "\n", "\r" }, StringSplitOptions.None),
                Path.GetFileName(path),
                forceIni,
                true);
        }

        public static EqProfile ParsePeaceText(string rawText, string defaultLabel)
        {
            if (string.IsNullOrEmpty(rawText) || rawText.Trim().Length == 0)
            {
                throw new InvalidDataException("貼り付けられたテキストが空です。");
            }
            string fallback = string.IsNullOrEmpty(defaultLabel) ? "AI・テキスト貼付" : defaultLabel;
            return ParsePeaceLinesCore(
                rawText.Split(new string[] { "\r\n", "\n", "\r" }, StringSplitOptions.None),
                fallback,
                false,
                false);
        }

        private static EqProfile ParsePeaceLinesCore(string[] lines, string defaultLabel, bool looksLikeIni, bool preferFileLabel)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                string trimmed = lines[i].Trim();
                if (trimmed.Length == 0) continue;
                if (trimmed.StartsWith("FilterCurve:", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(
                        "このデータは Audacity 用のエクスポートデータ（FilterCurve形式）のため読み込めません。\r\n\r\n" +
                        "Peace の「エクスポート」ボタンを押す必要はありません。\r\n" +
                        "C:\\Program Files\\EqualizerAPO\\config フォルダ内にある「〇〇.peace」または「peace.txt」を直接開いてください。");
                }
                if (string.Equals(trimmed, "[Frequencies]", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(trimmed, "[General]", StringComparison.OrdinalIgnoreCase))
                {
                    looksLikeIni = true;
                }
            }

            if (looksLikeIni)
            {
                return ParsePeaceNativeIni(lines, defaultLabel);
            }
            return ParseEqualizerApoText(lines, defaultLabel, preferFileLabel);
        }

        private static EqProfile ParsePeaceNativeIni(string[] lines, string fileName)
        {
            double preampDb = 0.0;
            SortedDictionary<int, double> freqs = new SortedDictionary<int, double>();
            Dictionary<int, double> gains = new Dictionary<int, double>();
            Dictionary<int, double> quals = new Dictionary<int, double>();
            Dictionary<int, int> filters = new Dictionary<int, int>();
            Dictionary<int, bool> disabled = new Dictionary<int, bool>();

            string section = "";
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith(";") || line.StartsWith("#")) continue;

                Match sm = SectionRx.Match(line);
                if (sm.Success)
                {
                    section = sm.Groups[1].Value.Trim();
                    continue;
                }

                // Only parse all-channel base sections (ignore per-speaker [Frequencies1]..[Frequencies8])
                if (string.Equals(section, "General", StringComparison.OrdinalIgnoreCase))
                {
                    int eqPos = line.IndexOf('=');
                    if (eqPos > 0)
                    {
                        string k = line.Substring(0, eqPos).Trim();
                        string v = line.Substring(eqPos + 1).Trim();
                        double parsedPre;
                        if (string.Equals(k, "PreAmp", StringComparison.OrdinalIgnoreCase))
                        {
                            if (!TryParseStrictNumber(v, out parsedPre))
                            {
                                throw new InvalidDataException(string.Format("行 {0}: PreAmp の数値が不正です ({1})。", i + 1, line));
                            }
                            preampDb = parsedPre;
                        }
                    }
                    continue;
                }

                Match kv = KeyValRx.Match(line);
                if (!kv.Success || string.IsNullOrEmpty(kv.Groups[2].Value)) continue;

                int idx;
                if (!int.TryParse(kv.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out idx) || idx < 1)
                {
                    continue;
                }

                string keyPrefix = kv.Groups[1].Value;
                string valStr = kv.Groups[3].Value;

                if (string.Equals(section, "Frequencies", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(keyPrefix, "Frequency", StringComparison.OrdinalIgnoreCase))
                {
                    double fv;
                    if (!TryParseStrictNumber(valStr, out fv))
                    {
                        throw new InvalidDataException(string.Format("行 {0}: Frequency{1} の数値が不正です ({2})。", i + 1, idx, line));
                    }
                    freqs[idx] = fv;
                }
                else if (string.Equals(section, "Gains", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(keyPrefix, "Gain", StringComparison.OrdinalIgnoreCase))
                {
                    double gv;
                    if (!TryParseStrictNumber(valStr, out gv))
                    {
                        throw new InvalidDataException(string.Format("行 {0}: Gain{1} の数値が不正です ({2})。", i + 1, idx, line));
                    }
                    gains[idx] = gv;
                }
                else if (string.Equals(section, "Qualities", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(keyPrefix, "Quality", StringComparison.OrdinalIgnoreCase))
                {
                    double qv;
                    if (!TryParseStrictNumber(valStr, out qv))
                    {
                        throw new InvalidDataException(string.Format("行 {0}: Quality{1} の数値が不正です ({2})。", i + 1, idx, line));
                    }
                    quals[idx] = qv;
                }
                else if (string.Equals(section, "Filters", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(keyPrefix, "Filter", StringComparison.OrdinalIgnoreCase))
                {
                    int fc;
                    if (int.TryParse(valStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out fc))
                    {
                        filters[idx] = fc;
                    }
                }
                else if (string.Equals(section, "Disabled", StringComparison.OrdinalIgnoreCase) &&
                         string.Equals(keyPrefix, "Disabled", StringComparison.OrdinalIgnoreCase))
                {
                    int dv;
                    if (int.TryParse(valStr, NumberStyles.Integer, CultureInfo.InvariantCulture, out dv) && dv != 0)
                    {
                        disabled[idx] = true;
                    }
                }
            }

            if (freqs.Count == 0)
            {
                throw new InvalidDataException("Peace設定ファイル (.peace) 内に有効な周波数データが見つかりませんでした。");
            }

            List<EqBandSpec> parsedBands = new List<EqBandSpec>();
            foreach (KeyValuePair<int, double> kvp in freqs)
            {
                int slot = kvp.Key;
                double fc = kvp.Value;
                double gdb = gains.ContainsKey(slot) ? gains[slot] : 0.0;
                int fcode = filters.ContainsKey(slot) ? filters[slot] : 0;
                bool isOff = (disabled.ContainsKey(slot) && disabled[slot]) || fcode < 0;

                FilterKind kind;
                if (fcode <= 0)
                {
                    kind = FilterKind.PK;
                }
                else if (fcode == 4 || fcode == 8 || fcode == 14 || fcode == 16)
                {
                    kind = FilterKind.LSC;
                }
                else if (fcode == 5 || fcode == 9 || fcode == 15 || fcode == 17)
                {
                    kind = FilterKind.HSC;
                }
                else
                {
                    if (isOff || Math.Abs(gdb) < 0.001)
                    {
                        isOff = true;
                        kind = FilterKind.PK;
                    }
                    else
                    {
                        throw new InvalidDataException(string.Format(
                            "スライダー {0} に未対応のフィルター種類 (Code {1}) が使われています。\r\n本機は PK (ピーキング) / LSC (ローシェルフ) / HSC (ハイシェルフ) に対応しています。",
                            slot, fcode));
                    }
                }

                double defaultQ = (kind == FilterKind.PK) ? 1.41 : 0.71;
                double q = quals.ContainsKey(slot) ? quals[slot] : defaultQ;
                if ((fcode == 8 || fcode == 9) && q > 3.0)
                {
                    q = 0.71;
                }

                EqBandSpec spec = new EqBandSpec();
                spec.Enabled = !isOff;
                spec.Kind = kind;
                spec.FrequencyHz = Clamp(fc, 10.0, 23500.0);
                spec.GainDb = Clamp(gdb, -30.0, 24.0);
                spec.Q = Clamp(q, 0.06, 30.0);

                if (spec.Enabled)
                {
                    string err;
                    if (ComputeBiquad(spec, slot, out err) == null)
                    {
                        throw new InvalidDataException(err);
                    }
                }
                parsedBands.Add(spec);
            }

            return PackBandsIntoProfile(parsedBands, preampDb, fileName);
        }

        private static EqProfile ParseEqualizerApoText(string[] lines, string defaultLabel, bool preferFileLabel)
        {
            double preampDb = 0.0;
            string customPresetLabel = null;
            List<EqBandSpec> parsedBands = new List<EqBandSpec>();

            for (int i = 0; i < lines.Length; i++)
            {
                string rawTrim = lines[i].Replace('\u3000', ' ').Trim();
                if (rawTrim.Length == 0 || rawTrim.StartsWith("```")) continue;

                Match nm = PresetCommentRx.Match(rawTrim);
                if (nm.Success && string.IsNullOrEmpty(customPresetLabel))
                {
                    string candidate = nm.Groups[1].Value.Trim();
                    if (candidate.Length > 0 && candidate.Length <= 48)
                    {
                        customPresetLabel = candidate;
                    }
                    continue;
                }
                if (rawTrim.StartsWith("#") || rawTrim.StartsWith(";") || rawTrim.StartsWith("//"))
                {
                    continue;
                }

                string line = NormalizeEqDirectiveLine(rawTrim);
                if (line.Length == 0) continue;

                if (line.StartsWith("Channel:", StringComparison.OrdinalIgnoreCase))
                {
                    string chSpec = line.Substring(8).Trim().ToUpperInvariant();
                    if (chSpec != "ALL" && chSpec != "L R" && chSpec != "R L" && chSpec != "1 2" && chSpec != "FL FR")
                    {
                        throw new InvalidDataException(string.Format(
                            "行 {0}: 左右独立チャンネル指定 ({1}) には対応していません。両耳共通のステレオEQ設定を指定してください。",
                            i + 1, line));
                    }
                    continue;
                }

                if (line.StartsWith("GraphicEQ:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Include:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Convolution:", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException(string.Format(
                        "行 {0}: 未対応の Equalizer APO 構文 ({1}) が含まれています。\r\n本機はパラメトリックEQ形式 (Filter 1: ON PK/LSC/HSC ...) または .peace ファイルに対応しています。",
                        i + 1, line.Split(':')[0] + ":"));
                }

                Match pm = PreampRx.Match(line);
                if (pm.Success)
                {
                    double pVal;
                    if (!TryParseStrictNumber(pm.Groups[1].Value, out pVal))
                    {
                        throw new InvalidDataException(string.Format(
                            "行 {0}: Preamp の数値を読み取れませんでした ({1})。小数点にはピリオド (.) を使用してください。",
                            i + 1, rawTrim));
                    }
                    preampDb += pVal;
                    continue;
                }
                if (Regex.IsMatch(line, @"^Preamp\s*[:=]", RegexOptions.IgnoreCase))
                {
                    throw new InvalidDataException(string.Format(
                        "行 {0}: Preamp 行の書式が正しくありません ({1})。\r\n例: Preamp: -4.0 dB",
                        i + 1, rawTrim));
                }

                Match fh = FilterHeadRx.Match(line);
                if (fh.Success)
                {
                    bool isOn = string.Equals(fh.Groups[1].Value, "ON", StringComparison.OrdinalIgnoreCase);
                    string kindStr = fh.Groups[2].Value.ToUpperInvariant();
                    string rest = fh.Groups[3].Success ? fh.Groups[3].Value.Trim() : "";

                    if (kindStr == "NONE")
                    {
                        continue;
                    }

                    bool supportedKind = (kindStr == "PK" || kindStr == "PEQ" ||
                                          kindStr == "LSC" || kindStr == "LS" ||
                                          kindStr == "HSC" || kindStr == "HS");
                    if (!supportedKind)
                    {
                        if (!isOn) continue;
                        throw new InvalidDataException(string.Format(
                            "行 {0}: 未対応のフィルター種類 ({1}) が有効になっています。\r\n本機は PK (ピーキング) / LSC (ローシェルフ) / HSC (ハイシェルフ) のみ対応しています。\r\n該当行: {2}",
                            i + 1, kindStr, rawTrim));
                    }

                    Match fb = FilterBodyRx.Match(rest);
                    if (!fb.Success)
                    {
                        if (!isOn) continue;
                        throw new InvalidDataException(string.Format(
                            "行 {0}: Filter 行の書式を読み取れませんでした。\r\n例: Filter 1: ON PK Fc 1000 Hz Gain -2.5 dB Q 1.41\r\n該当行: {1}",
                            i + 1, rawTrim));
                    }

                    double fc, gdb;
                    if (!TryParseStrictNumber(fb.Groups[1].Value, out fc) ||
                        !TryParseStrictNumber(fb.Groups[2].Value, out gdb))
                    {
                        if (!isOn) continue;
                        throw new InvalidDataException(string.Format(
                            "行 {0}: 周波数またはゲインの数値が不正です（小数点にはピリオドを使用してください）。\r\n該当行: {1}",
                            i + 1, rawTrim));
                    }

                    double q;
                    if (fb.Groups[3].Success)
                    {
                        if (!TryParseStrictNumber(fb.Groups[3].Value, out q))
                        {
                            if (!isOn) continue;
                            throw new InvalidDataException(string.Format(
                                "行 {0}: Q値の数値が不正です。\r\n該当行: {1}",
                                i + 1, rawTrim));
                        }
                    }
                    else if (fb.Groups[4].Success)
                    {
                        double bwOct;
                        if (!TryParseStrictNumber(fb.Groups[4].Value, out bwOct))
                        {
                            if (!isOn) continue;
                            throw new InvalidDataException(string.Format(
                                "行 {0}: BW Oct の数値が不正です。\r\n該当行: {1}",
                                i + 1, rawTrim));
                        }
                        q = BandwidthOctavesToQ(bwOct);
                        if (q <= 0.0)
                        {
                            if (!isOn) continue;
                            throw new InvalidDataException(string.Format(
                                "行 {0}: BW Oct の値が範囲外です ({1})。",
                                i + 1, rawTrim));
                        }
                    }
                    else
                    {
                        q = (kindStr == "PK" || kindStr == "PEQ") ? 1.41 : 0.70710678;
                    }

                    EqBandSpec spec = new EqBandSpec();
                    spec.Enabled = isOn;
                    if (kindStr == "PK" || kindStr == "PEQ") spec.Kind = FilterKind.PK;
                    else if (kindStr == "LSC" || kindStr == "LS") spec.Kind = FilterKind.LSC;
                    else spec.Kind = FilterKind.HSC;

                    if (isOn && (fc < 10.0 || fc > 23500.0 || gdb < -30.0 || gdb > 24.0 || q < 0.06 || q > 30.0))
                    {
                        EqBandSpec rawCheck = new EqBandSpec();
                        rawCheck.Enabled = true;
                        rawCheck.Kind = spec.Kind;
                        rawCheck.FrequencyHz = fc;
                        rawCheck.GainDb = gdb;
                        rawCheck.Q = q;
                        string rangeErr;
                        ComputeBiquad(rawCheck, parsedBands.Count + 1, out rangeErr);
                        throw new InvalidDataException(string.Format("行 {0}: {1}\r\n該当行: {2}", i + 1, rangeErr, rawTrim));
                    }

                    spec.FrequencyHz = Clamp(fc, 10.0, 23500.0);
                    spec.GainDb = Clamp(gdb, -30.0, 24.0);
                    spec.Q = Clamp(q, 0.06, 30.0);

                    if (isOn)
                    {
                        string err;
                        if (ComputeBiquad(spec, parsedBands.Count + 1, out err) == null)
                        {
                            throw new InvalidDataException(string.Format("行 {0}: {1}\r\n該当行: {2}", i + 1, err, rawTrim));
                        }
                    }
                    parsedBands.Add(spec);
                    continue;
                }

                if (Regex.IsMatch(line, @"^Filter\s*\d*\s*:", RegexOptions.IgnoreCase))
                {
                    throw new InvalidDataException(string.Format(
                        "行 {0}: Filter 行の書式が正しくありません ({1})。",
                        i + 1, rawTrim));
                }
            }

            if (parsedBands.Count == 0)
            {
                throw new InvalidDataException(
                    "有効なフィルター行（Filter 1: ON ...）が見つかりませんでした。\r\n" +
                    "Equalizer APO 形式のテキスト、または Peace の .peace ファイルを指定してください。");
            }

            string finalLabel = (!preferFileLabel && !string.IsNullOrEmpty(customPresetLabel))
                ? customPresetLabel
                : defaultLabel;
            return PackBandsIntoProfile(parsedBands, preampDb, finalLabel);
        }

        public static string BuildAiEqPrompt(string deviceName, string goalText)
        {
            string dev = string.IsNullOrEmpty(deviceName) || deviceName.Trim().Length == 0
                ? "（ここに機種名を書く、または普段使っているEQ画面のスクショや数値を貼る）"
                : deviceName.Trim();
            string goal = string.IsNullOrEmpty(goalText) || goalText.Trim().Length == 0
                ? "（例：添付スクショのEQを再現したい ／ 低音のボワつきを抑えて声や足音を聞きやすくしたい 等）"
                : goalText.Trim();

            return
                "Biwamasu Mixer（48kHz / 最大11バンド・パラメトリックEQ搭載ミキサー）に設定するための Equalizer APO 互換イコライザーデータを作成してください。\r\n\r\n" +
                "【私の環境・やりたいこと】\r\n" +
                "・使用しているイヤホン／ヘッドホン：" + dev + "\r\n" +
                "・音の好み・目的：" + goal + "\r\n\r\n" +
                "【厳守してほしい出力フォーマット規則】\r\n" +
                "1. バンド数は最大 11 バンド以内（Filter 1 〜 Filter 11）。10バンド以下の固定スライダー式EQを変換する場合はすべて `PK`（Q値は 1.41 前後）にすること。\r\n" +
                "2. 使用可能なフィルター種類は以下の3種類のみ：\r\n" +
                "   - `PK`（ピーキング：10 <= Fc <= 23500 Hz, -30.0 <= Gain <= +24.0 dB, 0.10 <= Q <= 20.00）\r\n" +
                "   - `LSC`（ローシェルフ：標準 Q 0.71）\r\n" +
                "   - `HSC`（ハイシェルフ：標準 Q 0.71）\r\n" +
                "3. デジタル音割れ（0 dBFS超過）を防ぐため、合成周波数特性の最大ピークが 0.0 dB 以下になるよう、最大ブースト量に応じたマイナスの `Preamp: -X.X dB`（-36.0 〜 0.0 dB）を必ず記載すること。\r\n" +
                "4. 1行目に `# Preset: （わかりやすい短い設定名）` を付け、以下のコードブロック形式で出力すること：\r\n\r\n" +
                "# Preset: カスタムEQ\r\n" +
                "Preamp: -3.5 dB\r\n" +
                "Filter 1: ON LSC Fc 100 Hz Gain 3.0 dB Q 0.71\r\n" +
                "Filter 2: ON PK Fc 250 Hz Gain -1.5 dB Q 1.41\r\n\r\n" +
                "5. 回答の最後に「上のコードブロックをコピーして、Biwamasu-Mixer Setting tool.exe の『AI・テキスト貼付...』画面にそのまま貼り付けて適用してください」と案内してください。";
        }

        private static EqProfile PackBandsIntoProfile(List<EqBandSpec> parsedBands, double preampDb, string sourceLabel)
        {
            string preErr;
            if (!IsFinite(preampDb) || preampDb < -36.0 || preampDb > 12.0)
            {
                throw new InvalidDataException(string.Format(
                    "プリアンプ ({0:0.0} dB) が対応範囲外です。-36.0 〜 +12.0 dB の範囲で指定してください。",
                    preampDb));
            }
            if (ComputePreampLinear(preampDb, out preErr) <= 0.0f || preErr != null)
            {
                throw new InvalidDataException(preErr);
            }

            EqProfile profile = new EqProfile();
            profile.PreampDb = preampDb;
            profile.SourceLabel = sourceLabel;

            if (parsedBands.Count <= 11)
            {
                for (int i = 0; i < parsedBands.Count; i++)
                {
                    profile.Bands[i] = parsedBands[i];
                }
                for (int i = parsedBands.Count; i < 11; i++)
                {
                    profile.Bands[i].Enabled = false;
                    profile.Bands[i].GainDb = 0.0;
                }
                return profile;
            }

            // More than 11 sliders in file: keep non-zero enabled bands first, then zero-gain enabled bands
            List<EqBandSpec> nonZeroActive = new List<EqBandSpec>();
            List<EqBandSpec> zeroOrOff = new List<EqBandSpec>();
            for (int i = 0; i < parsedBands.Count; i++)
            {
                EqBandSpec b = parsedBands[i];
                if (b.Enabled && Math.Abs(b.GainDb) > 0.001) nonZeroActive.Add(b);
                else zeroOrOff.Add(b);
            }

            if (nonZeroActive.Count > 11)
            {
                throw new InvalidDataException(string.Format(
                    "ゲインが設定されているバンドが {0} 個あります（本機の最大対応数は 11 バンドです）。",
                    nonZeroActive.Count));
            }

            int outIdx = 0;
            for (int i = 0; i < nonZeroActive.Count && outIdx < 11; i++)
            {
                profile.Bands[outIdx++] = nonZeroActive[i];
            }
            for (int i = 0; i < zeroOrOff.Count && outIdx < 11; i++)
            {
                profile.Bands[outIdx++] = zeroOrOff[i];
            }
            return profile;
        }

        public static void SavePeaceFile(string path, EqProfile profile)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# Biwamasu Mixer Equalizer Preset (Equalizer APO / Peace format)");
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Preamp: {0:0.0} dB", profile.PreampDb));
            for (int i = 0; i < 11; i++)
            {
                EqBandSpec b = profile.Bands[i];
                string state = b.Enabled ? "ON" : "OFF";
                string kind = b.Kind == FilterKind.PK ? "PK" : (b.Kind == FilterKind.LSC ? "LSC" : "HSC");
                string fcStr = FormatFrequencyClean(b.FrequencyHz);
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "Filter {0}: {1} {2} Fc {3} Hz Gain {4:0.0} dB Q {5:0.00}",
                    i + 1, state, kind, fcStr, b.GainDb, b.Q));
            }
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        public static double Clamp(double v, double min, double max)
        {
            if (double.IsNaN(v)) return min;
            if (v < min) return min;
            if (v > max) return max;
            return v;
        }

        public static bool BiquadCoeffStable(float[] c)
        {
            if (c == null || c.Length < 5) return false;
            if (!FloatSane(c[0]) || !FloatSane(c[1]) || !FloatSane(c[2]) || !FloatSane(c[3]) || !FloatSane(c[4]))
            {
                return false;
            }
            if (!(c[4] > -1.0f && c[4] < 1.0f)) return false;
            return (1.0f + c[4]) > Math.Abs(c[3]);
        }

        public static uint ComputeCrc32(byte[] data)
        {
            uint c = 0xffffffffu;
            for (int i = 0; i < data.Length; i++)
            {
                c ^= data[i];
                for (int k = 0; k < 8; k++)
                {
                    c = ((c & 1u) != 0u) ? ((c >> 1) ^ 0xedb88320u) : (c >> 1);
                }
            }
            return ~c;
        }

        public static bool CoeffsMatchProfile(EqProfile profile, float devPreamp, float[][] devCoeffs)
        {
            if (profile == null || devCoeffs == null || devCoeffs.Length != 11) return false;
            string err;
            float expPre = ComputePreampLinear(profile.PreampDb, out err);
            if (err != null || Math.Abs(expPre - devPreamp) > 1.5e-3f) return false;

            for (int i = 0; i < 11; i++)
            {
                float[] expC = ComputeBiquad(profile.Bands[i], i + 1, out err);
                if (expC == null || devCoeffs[i] == null || devCoeffs[i].Length < 5) return false;
                for (int j = 0; j < 5; j++)
                {
                    if (Math.Abs(expC[j] - devCoeffs[i][j]) > 2.0e-4f) return false;
                }
            }
            return true;
        }

        public static bool AreAllCoeffsFlat(float devPreamp, float[][] devCoeffs)
        {
            if (Math.Abs(devPreamp - 1.0f) > 1.0e-4f || devCoeffs == null || devCoeffs.Length != 11) return false;
            for (int i = 0; i < 11; i++)
            {
                float[] c = devCoeffs[i];
                if (c == null || c.Length < 5) return false;
                if (Math.Abs(c[0] - 1.0f) > 1.0e-5f || Math.Abs(c[1]) > 1.0e-5f ||
                    Math.Abs(c[2]) > 1.0e-5f || Math.Abs(c[3]) > 1.0e-5f || Math.Abs(c[4]) > 1.0e-5f)
                {
                    return false;
                }
            }
            return true;
        }

        public static bool TryReconstructProfileFromCoeffs(float devPreamp, float[][] devCoeffs, string label, out EqProfile result)
        {
            result = null;
            if (!FloatSane(devPreamp) || devPreamp < 0.01f || devPreamp > 4.0f || devCoeffs == null || devCoeffs.Length != 11)
            {
                return false;
            }

            EqProfile p = new EqProfile();
            p.PreampDb = Clamp(Math.Round(20.0 * Math.Log10(devPreamp) * 10.0) / 10.0, -36.0, 12.0);
            p.SourceLabel = string.IsNullOrEmpty(label) ? "本体メモリ保存済み" : label;

            const double fs = 48000.0;
            for (int i = 0; i < 11; i++)
            {
                float[] c = devCoeffs[i];
                if (!BiquadCoeffStable(c)) return false;

                double nb0 = c[0], nb1 = c[1], nb2 = c[2], na1 = c[3], na2 = c[4];
                if (Math.Abs(nb0 - 1.0) < 1.0e-5 && Math.Abs(nb1) < 1.0e-5 &&
                    Math.Abs(nb2) < 1.0e-5 && Math.Abs(na1) < 1.0e-5 && Math.Abs(na2) < 1.0e-5)
                {
                    p.Bands[i].Enabled = false;
                    p.Bands[i].GainDb = 0.0;
                    continue;
                }

                double denom1MinusA2 = 1.0 - na2;
                double denom1PlusA2 = 1.0 + na2;
                if (Math.Abs(denom1MinusA2) < 1.0e-7 || Math.Abs(denom1PlusA2) < 1.0e-7)
                {
                    p.Bands[i].Enabled = false;
                    p.Bands[i].GainDb = 0.0;
                    continue;
                }

                FilterKind kind;
                double fc, gdb, q;
                double sumPlus = 1.0 + na1 + na2;
                double sumMinus = 1.0 - na1 + na2;
                double numPlus = nb0 + nb1 + nb2;
                double numMinus = nb0 - nb1 + nb2;
                if (sumPlus <= 1.0e-11 || sumMinus <= 1.0e-11 || numPlus <= 1.0e-11 || numMinus <= 1.0e-11)
                {
                    p.Bands[i].Enabled = false;
                    p.Bands[i].GainDb = 0.0;
                    continue;
                }

                double dcDb = 20.0 * Math.Log10(numPlus / sumPlus);
                double nyqDb = 20.0 * Math.Log10(numMinus / sumMinus);

                if (Math.Abs(dcDb) < 0.08 && Math.Abs(nyqDb) < 0.08 && Math.Abs(nb1 - na1) <= 1.0e-3)
                {
                    kind = FilterKind.PK;
                    double aSq = (nb0 - nb2) / denom1MinusA2;
                    if (aSq <= 1.0e-6) return false;
                    double a = Math.Sqrt(aSq);
                    double a0 = 2.0 / denom1PlusA2;
                    double w0 = 2.0 * Math.Atan(Math.Sqrt(sumPlus / sumMinus));
                    double alpha = 0.5 * denom1MinusA2 * a0 * a;
                    if (alpha <= 1.0e-9) return false;
                    fc = w0 * fs / (2.0 * Math.PI);
                    gdb = 40.0 * Math.Log10(a);
                    q = Math.Sin(w0) / (2.0 * alpha);
                }
                else
                {
                    double a = (nb0 - nb2) / denom1MinusA2;
                    if (a <= 1.0e-4) return false;
                    if (Math.Abs(a - 1.0) < 1.0e-4)
                    {
                        p.Bands[i].Enabled = false;
                        p.Bands[i].GainDb = 0.0;
                        continue;
                    }
                    double sumDenom = (nb0 + nb2) / a + denom1PlusA2;
                    if (Math.Abs(sumDenom) < 1.0e-7) return false;
                    double a0 = 4.0 * (a + 1.0) / sumDenom;
                    kind = (Math.Abs(dcDb) >= Math.Abs(nyqDb)) ? FilterKind.LSC : FilterKind.HSC;
                    double tanHalfSq = (kind == FilterKind.LSC)
                        ? (a * sumPlus / sumMinus)
                        : (sumPlus / (a * sumMinus));
                    double w0 = 2.0 * Math.Atan(Math.Sqrt(Math.Max(1.0e-12, tanHalfSq)));
                    double sa = 0.5 * denom1MinusA2 * a0;
                    double alpha = sa / (2.0 * Math.Sqrt(a));
                    if (alpha <= 1.0e-9) return false;
                    fc = w0 * fs / (2.0 * Math.PI);
                    gdb = 40.0 * Math.Log10(a);
                    q = Math.Sin(w0) / (2.0 * alpha);
                }

                p.Bands[i].Enabled = true;
                p.Bands[i].Kind = kind;
                p.Bands[i].FrequencyHz = Clamp(Math.Round(fc * 10.0) / 10.0, 10.0, 23500.0);
                p.Bands[i].GainDb = Clamp(Math.Round(gdb * 10.0) / 10.0, -30.0, 24.0);
                p.Bands[i].Q = Clamp(Math.Round(q * 100.0) / 100.0, 0.06, 30.0);
            }

            result = p;
            return true;
        }
    }

    internal sealed class GainSlider : Control
    {
        private double _minDb = -60.0;
        private double _maxDb = 6.0;
        private double _valueDb = -45.0;
        private double _defaultResetDb = 0.0;
        private double _dangerThresholdDb = double.PositiveInfinity;
        private bool _bipolarFromZero = false;
        private bool _showScaleLabels = true;
        private double[] _scaleTicks = new double[] { -60, -45, -30, -15, 0, 6 };
        private bool _dragging = false;
        private bool _interacting = false;
        private bool _thumbGrabbedWithoutMove = false;
        private bool _pendingGrooveClick = false;
        private bool _grooveReleased = false;
        private int _downX = 0;
        private bool _hovering = false;
        private readonly Font _tickFont;
        private readonly System.Windows.Forms.Timer _grooveClickTimer;

        public event EventHandler ValueChanged;
        public event EventHandler DragStarted;
        public event EventHandler DragCompleted;

        public GainSlider()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint |
                ControlStyles.Selectable,
                true);
            TabStop = true;
            _tickFont = UiScale.CreateSmallFont();
            _grooveClickTimer = new System.Windows.Forms.Timer();
            _grooveClickTimer.Interval = 240;
            _grooveClickTimer.Tick += OnGrooveClickTimerTick;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_grooveClickTimer != null)
                {
                    _grooveClickTimer.Stop();
                    _grooveClickTimer.Dispose();
                }
                if (_tickFont != null)
                {
                    _tickFont.Dispose();
                }
            }
            base.Dispose(disposing);
        }

        public double MinimumDb
        {
            get { return _minDb; }
            set { _minDb = value; Invalidate(); }
        }

        public double MaximumDb
        {
            get { return _maxDb; }
            set { _maxDb = value; Invalidate(); }
        }

        public double ValueDb
        {
            get { return _valueDb; }
            set
            {
                double clamped = EqDspMath.Clamp(Math.Round(value * 10.0) / 10.0, _minDb, _maxDb);
                if (Math.Abs(clamped - _valueDb) > 0.001)
                {
                    _valueDb = clamped;
                    Invalidate();
                    if (ValueChanged != null) ValueChanged(this, EventArgs.Empty);
                }
            }
        }

        public void SetValueSilent(double value)
        {
            if (_pendingGrooveClick)
            {
                _grooveClickTimer.Stop();
                _pendingGrooveClick = false;
                _grooveReleased = false;
                EndInteraction();
            }
            double clamped = EqDspMath.Clamp(Math.Round(value * 10.0) / 10.0, _minDb, _maxDb);
            if (Math.Abs(clamped - _valueDb) > 0.001)
            {
                _valueDb = clamped;
                Invalidate();
            }
        }

        public double DefaultResetDb
        {
            get { return _defaultResetDb; }
            set { _defaultResetDb = value; }
        }

        public double DangerThresholdDb
        {
            get { return _dangerThresholdDb; }
            set
            {
                if (Math.Abs(_dangerThresholdDb - value) > 0.01 || (double.IsInfinity(_dangerThresholdDb) != double.IsInfinity(value)))
                {
                    _dangerThresholdDb = value;
                    Invalidate();
                }
            }
        }

        public bool BipolarFromZero
        {
            get { return _bipolarFromZero; }
            set { _bipolarFromZero = value; Invalidate(); }
        }

        public bool ShowScaleLabels
        {
            get { return _showScaleLabels; }
            set { _showScaleLabels = value; Invalidate(); }
        }

        public double[] ScaleTicks
        {
            get { return _scaleTicks; }
            set { _scaleTicks = value; Invalidate(); }
        }

        public bool IsInDangerZone
        {
            get { return !double.IsInfinity(_dangerThresholdDb) && _valueDb > _dangerThresholdDb + 0.04; }
        }

        private Rectangle GetTrackBounds()
        {
            int padX = UiScale.Px(10);
            int w = Math.Max(20, ClientSize.Width - padX * 2);
            int y = _showScaleLabels ? UiScale.Px(11) : (ClientSize.Height / 2);
            int h = Math.Max(4, UiScale.Px(5));
            return new Rectangle(padX, y - h / 2, w, h);
        }

        private int DbToX(double db, Rectangle track)
        {
            if (_maxDb <= _minDb) return track.Left;
            double t = (EqDspMath.Clamp(db, _minDb, _maxDb) - _minDb) / (_maxDb - _minDb);
            return track.Left + (int)Math.Round(t * track.Width);
        }

        private double XToDb(int x, Rectangle track)
        {
            if (track.Width <= 1) return _minDb;
            double t = (double)(x - track.Left) / track.Width;
            t = EqDspMath.Clamp(t, 0.0, 1.0);
            double raw = _minDb + t * (_maxDb - _minDb);

            if (_bipolarFromZero && _minDb < 0.0 && _maxDb > 0.0)
            {
                int zeroX = DbToX(0.0, track);
                if (Math.Abs(x - zeroX) <= UiScale.Px(3))
                {
                    return 0.0;
                }
            }
            return Math.Round(raw * 10.0) / 10.0;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.Clear(BackColor);

            Rectangle track = GetTrackBounds();
            bool enabled = Enabled;

            bool hasDangerRegion = !double.IsInfinity(_dangerThresholdDb) && _dangerThresholdDb < _maxDb - 0.01;
            int dangerStartX = track.Right;
            if (hasDangerRegion)
            {
                double startDb = Math.Max(_minDb, _dangerThresholdDb);
                dangerStartX = DbToX(startDb, track);
                if (dangerStartX < track.Right)
                {
                    int bandTop = Math.Max(1, track.Top - UiScale.Px(5));
                    int bandBottom = _showScaleLabels ? Math.Min(ClientSize.Height - 1, track.Bottom + UiScale.Px(8)) : Math.Min(ClientSize.Height - 2, track.Bottom + UiScale.Px(5));
                    Rectangle dangerBg = new Rectangle(dangerStartX, bandTop, track.Right - dangerStartX, Math.Max(4, bandBottom - bandTop));
                    using (SolidBrush pinkBrush = new SolidBrush(enabled ? Color.FromArgb(255, 226, 226) : Color.FromArgb(240, 232, 232)))
                    {
                        g.FillRectangle(pinkBrush, dangerBg);
                    }
                    if (_dangerThresholdDb > _minDb + 0.01)
                    {
                        using (Pen boundaryPen = new Pen(Color.FromArgb(204, 60, 60), 1f))
                        {
                            g.DrawLine(boundaryPen, dangerStartX, bandTop, dangerStartX, bandBottom);
                        }
                    }
                }
            }

            using (SolidBrush grooveSafe = new SolidBrush(enabled ? Color.FromArgb(222, 225, 230) : Color.FromArgb(232, 232, 232)))
            using (Pen grooveBorder = new Pen(enabled ? Color.FromArgb(172, 176, 184) : Color.FromArgb(204, 204, 204), 1f))
            {
                g.FillRectangle(grooveSafe, track);
                g.DrawRectangle(grooveBorder, track.Left, track.Top, track.Width - 1, track.Height - 1);
            }

            if (hasDangerRegion && dangerStartX < track.Right)
            {
                Rectangle redGroove = new Rectangle(dangerStartX, track.Top, track.Right - dangerStartX, track.Height);
                using (SolidBrush redFill = new SolidBrush(enabled ? Color.FromArgb(232, 112, 112) : Color.FromArgb(220, 180, 180)))
                using (Pen redBorder = new Pen(enabled ? Color.FromArgb(188, 44, 44) : Color.FromArgb(196, 150, 150), 1f))
                {
                    g.FillRectangle(redFill, redGroove);
                    g.DrawRectangle(redBorder, redGroove.Left, redGroove.Top, Math.Max(1, redGroove.Width - 1), redGroove.Height - 1);
                }
            }

            int valX = DbToX(_valueDb, track);
            int originX = (_bipolarFromZero && _minDb <= 0.0 && _maxDb >= 0.0) ? DbToX(0.0, track) : track.Left;
            int fillLeft = Math.Min(originX, valX);
            int fillWidth = Math.Abs(valX - originX);
            if (fillWidth > 0 && enabled)
            {
                Color activeColor = IsInDangerZone
                    ? Color.FromArgb(212, 36, 36)
                    : Color.FromArgb(0, 120, 215);
                using (SolidBrush activeBrush = new SolidBrush(activeColor))
                {
                    g.FillRectangle(activeBrush, fillLeft, track.Top + 1, fillWidth, Math.Max(2, track.Height - 2));
                }
            }

            if (_scaleTicks != null && _scaleTicks.Length > 0)
            {
                using (Pen tickPen = new Pen(Color.FromArgb(156, 160, 168), 1f))
                using (Pen zeroTickPen = new Pen(Color.FromArgb(90, 96, 108), 1f))
                using (SolidBrush lblBrush = new SolidBrush(enabled ? Color.FromArgb(96, 100, 108) : SystemColors.GrayText))
                using (SolidBrush redLblBrush = new SolidBrush(Color.FromArgb(172, 32, 32)))
                {
                    for (int i = 0; i < _scaleTicks.Length; i++)
                    {
                        double td = _scaleTicks[i];
                        if (td < _minDb - 0.01 || td > _maxDb + 0.01) continue;
                        int tx = DbToX(td, track);
                        bool isZero = Math.Abs(td) < 0.05;
                        int tickTop = track.Bottom + UiScale.Px(2);
                        int tickBot = tickTop + UiScale.Px(isZero ? 5 : 3);
                        g.DrawLine(isZero ? zeroTickPen : tickPen, tx, tickTop, tx, tickBot);

                        if (_showScaleLabels)
                        {
                            string s = td > 0.05
                                ? string.Format(CultureInfo.InvariantCulture, "+{0:0}", td)
                                : string.Format(CultureInfo.InvariantCulture, "{0:0}", td);
                            SizeF sz = g.MeasureString(s, _tickFont);
                            float lx = tx - sz.Width / 2f;
                            if (lx < 0f) lx = 0f;
                            if (lx + sz.Width > ClientSize.Width) lx = ClientSize.Width - sz.Width;
                            bool tickInDanger = hasDangerRegion && td > _dangerThresholdDb + 0.04;
                            g.DrawString(s, _tickFont, (tickInDanger && enabled) ? redLblBrush : lblBrush, lx, tickBot);
                        }
                    }
                }
            }
            else if (_bipolarFromZero && _minDb < 0.0 && _maxDb > 0.0)
            {
                int zx = DbToX(0.0, track);
                using (Pen zp = new Pen(Color.FromArgb(130, 136, 146), 1f))
                {
                    g.DrawLine(zp, zx, track.Top - UiScale.Px(3), zx, track.Bottom + UiScale.Px(3));
                }
            }

            int thumbW = UiScale.Px(10);
            int thumbH = UiScale.Px(17);
            int thumbCenterY = track.Top + track.Height / 2;
            Rectangle thumb = new Rectangle(valX - thumbW / 2, thumbCenterY - thumbH / 2, thumbW, thumbH);

            Color thumbFill;
            Color thumbBorder;
            if (!enabled)
            {
                thumbFill = Color.FromArgb(234, 234, 234);
                thumbBorder = Color.FromArgb(186, 186, 186);
            }
            else if (IsInDangerZone)
            {
                thumbFill = (_dragging || _hovering) ? Color.FromArgb(228, 48, 48) : Color.FromArgb(208, 34, 34);
                thumbBorder = Color.FromArgb(140, 16, 16);
            }
            else
            {
                thumbFill = _dragging
                    ? Color.FromArgb(0, 98, 184)
                    : (_hovering ? Color.FromArgb(24, 134, 226) : Color.FromArgb(0, 120, 215));
                thumbBorder = Color.FromArgb(0, 84, 153);
            }

            using (SolidBrush tb = new SolidBrush(thumbFill))
            using (Pen tp = new Pen(thumbBorder, 1f))
            {
                g.FillRectangle(tb, thumb);
                g.DrawRectangle(tp, thumb.Left, thumb.Top, thumb.Width - 1, thumb.Height - 1);
            }

            if (Focused && ShowFocusCues)
            {
                Rectangle focusRect = ClientRectangle;
                focusRect.Inflate(-1, -1);
                ControlPaint.DrawFocusRectangle(g, focusRect);
            }
        }

        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            _hovering = true;
            Invalidate();
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            _hovering = false;
            Invalidate();
        }

        private void BeginInteraction()
        {
            if (!_interacting)
            {
                _interacting = true;
                if (DragStarted != null) DragStarted(this, EventArgs.Empty);
            }
        }

        private void EndInteraction()
        {
            if (_interacting)
            {
                _interacting = false;
                if (DragCompleted != null) DragCompleted(this, EventArgs.Empty);
            }
        }

        private void OnGrooveClickTimerTick(object sender, EventArgs e)
        {
            _grooveClickTimer.Stop();
            if (!_pendingGrooveClick) return;
            _pendingGrooveClick = false;

            if (!Enabled)
            {
                _grooveReleased = false;
                EndInteraction();
                return;
            }

            if (_grooveReleased)
            {
                _grooveReleased = false;
                ValueDb = XToDb(_downX, GetTrackBounds());
                EndInteraction();
            }
            else
            {
                _dragging = true;
                _thumbGrabbedWithoutMove = false;
                Invalidate();
                ValueDb = XToDb(_downX, GetTrackBounds());
            }
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (!Enabled)
            {
                _grooveClickTimer.Stop();
                _pendingGrooveClick = false;
                _grooveReleased = false;
                _dragging = false;
                _thumbGrabbedWithoutMove = false;
                if (Capture) Capture = false;
                EndInteraction();
                Invalidate();
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled || e.Button != MouseButtons.Left) return;
            Focus();

            if (e.Clicks >= 2 || _pendingGrooveClick)
            {
                _grooveClickTimer.Stop();
                _pendingGrooveClick = false;
                _grooveReleased = false;
                _dragging = false;
                _thumbGrabbedWithoutMove = false;
                if (Capture) Capture = false;
                BeginInteraction();
                ValueDb = _defaultResetDb;
                Invalidate();
                EndInteraction();
                return;
            }

            Rectangle track = GetTrackBounds();
            int thumbX = DbToX(_valueDb, track);
            _downX = e.X;

            if (Math.Abs(e.X - thumbX) <= UiScale.Px(8))
            {
                _dragging = true;
                _thumbGrabbedWithoutMove = true;
                Capture = true;
                BeginInteraction();
                Invalidate();
            }
            else
            {
                _pendingGrooveClick = true;
                _grooveReleased = false;
                Capture = true;
                BeginInteraction();
                _grooveClickTimer.Stop();
                _grooveClickTimer.Start();
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!Enabled) return;

            if (_pendingGrooveClick && !_grooveReleased && Math.Abs(e.X - _downX) >= UiScale.Px(4))
            {
                _grooveClickTimer.Stop();
                _pendingGrooveClick = false;
                _dragging = true;
                _thumbGrabbedWithoutMove = false;
                Invalidate();
            }

            if (_dragging)
            {
                if (_thumbGrabbedWithoutMove)
                {
                    if (Math.Abs(e.X - _downX) < UiScale.Px(2)) return;
                    _thumbGrabbedWithoutMove = false;
                }
                ValueDb = XToDb(e.X, GetTrackBounds());
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (_pendingGrooveClick && !_grooveReleased)
            {
                _grooveReleased = true;
                if (Capture) Capture = false;
                return;
            }
            if (_dragging)
            {
                _dragging = false;
                _thumbGrabbedWithoutMove = false;
                Capture = false;
                Invalidate();
                EndInteraction();
            }
        }

        protected override void OnMouseCaptureChanged(EventArgs e)
        {
            base.OnMouseCaptureChanged(e);
            if (_pendingGrooveClick && !_grooveReleased && !Capture)
            {
                _grooveClickTimer.Stop();
                _pendingGrooveClick = false;
                EndInteraction();
                return;
            }
            if (_dragging && !Capture)
            {
                _dragging = false;
                _thumbGrabbedWithoutMove = false;
                Invalidate();
                EndInteraction();
            }
        }

        internal static bool VerifyGrooveDoubleClickSafetyForTest()
        {
            using (GainSlider gs = new GainSlider())
            {
                gs.Size = new Size(300, 34);
                gs.MinimumDb = -60.0;
                gs.MaximumDb = 6.0;
                gs.DefaultResetDb = -45.0;
                gs.SetValueSilent(-30.0);

                List<double> seen = new List<double>();
                gs.ValueChanged += delegate { seen.Add(gs.ValueDb); };

                // Simulate double-clicking near the +6 dB right end of the groove
                gs.OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, 288, 12, 0));
                gs.OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, 288, 12, 0));
                gs.OnMouseDown(new MouseEventArgs(MouseButtons.Left, 2, 288, 12, 0));
                gs.OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, 288, 12, 0));

                if (Math.Abs(gs.ValueDb - (-45.0)) > 0.01) return false;
                for (int i = 0; i < seen.Count; i++)
                {
                    if (seen[i] > -30.0) return false;
                }
                if (gs._interacting || gs._pendingGrooveClick || gs._dragging) return false;

                // Simulate dragging from groove (immediate response once moved >= 4px)
                gs.OnMouseDown(new MouseEventArgs(MouseButtons.Left, 1, 250, 12, 0));
                gs.OnMouseMove(new MouseEventArgs(MouseButtons.Left, 1, 260, 12, 0));
                gs.OnMouseUp(new MouseEventArgs(MouseButtons.Left, 1, 260, 12, 0));
                if (gs.ValueDb <= -30.0 || gs._interacting) return false;
                return true;
            }
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            base.OnMouseWheel(e);
            if (!Enabled || !Focused) return;
            double delta = e.Delta > 0 ? 0.5 : -0.5;
            ValueDb = _valueDb + delta;
            if (DragCompleted != null) DragCompleted(this, EventArgs.Empty);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.Left || k == Keys.Right || k == Keys.Up || k == Keys.Down)
            {
                return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!Enabled) return;
            if (e.KeyCode == Keys.Left || e.KeyCode == Keys.Down)
            {
                ValueDb = _valueDb - 0.5;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Right || e.KeyCode == Keys.Up)
            {
                ValueDb = _valueDb + 0.5;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.PageDown)
            {
                ValueDb = _valueDb - 3.0;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.PageUp)
            {
                ValueDb = _valueDb + 3.0;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.Home)
            {
                ValueDb = _minDb;
                e.Handled = true;
            }
            else if (e.KeyCode == Keys.End)
            {
                ValueDb = _maxDb;
                e.Handled = true;
            }
        }
    }

    internal sealed class EqResponseGraph : Control
    {
        private EqProfile _profile;
        private bool _eqEnabled = true;
        private int _selectedBand = -1;
        private double _peakWithPreampDb = 0.0;
        private double _rawFilterPeakDb = 0.0;
        private readonly PointF[] _bandMarkerCenters = new PointF[11];
        private readonly Font _axisFont;
        private readonly Font _badgeFont;

        public event Action<int> BandDotClicked;

        public EqResponseGraph()
        {
            SetStyle(
                ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer |
                ControlStyles.ResizeRedraw |
                ControlStyles.UserPaint,
                true);
            _axisFont = UiScale.CreateSmallFont();
            _badgeFont = UiScale.CreateSmallFont();
            _profile = new EqProfile();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (_axisFont != null) _axisFont.Dispose();
                if (_badgeFont != null) _badgeFont.Dispose();
            }
            base.Dispose(disposing);
        }

        public double PeakWithPreampDb { get { return _peakWithPreampDb; } }
        public double RawFilterPeakDb { get { return _rawFilterPeakDb; } }

        public int SelectedBand
        {
            get { return _selectedBand; }
            set
            {
                if (_selectedBand != value)
                {
                    _selectedBand = value;
                    Invalidate();
                }
            }
        }

        public void SetProfile(EqProfile profile, bool eqEnabled)
        {
            _profile = profile != null ? profile.Clone() : new EqProfile();
            _eqEnabled = eqEnabled;
            _rawFilterPeakDb = EqDspMath.ComputePeakFilterBoostDb(_profile, out _peakWithPreampDb);
            Invalidate();
        }

        private Rectangle GetPlotArea()
        {
            int left = UiScale.Px(36);
            int top = UiScale.Px(8);
            int right = UiScale.Px(10);
            int bottom = UiScale.Px(18);
            return new Rectangle(
                left,
                top,
                Math.Max(40, ClientSize.Width - left - right),
                Math.Max(30, ClientSize.Height - top - bottom));
        }

        private static float FreqToX(double hz, Rectangle plot)
        {
            double logMin = Math.Log10(20.0);
            double logMax = Math.Log10(20000.0);
            double t = (Math.Log10(EqDspMath.Clamp(hz, 20.0, 20000.0)) - logMin) / (logMax - logMin);
            return plot.Left + (float)(t * plot.Width);
        }

        private static float DbToY(double db, Rectangle plot)
        {
            const double minDb = -15.0;
            const double maxDb = 15.0;
            double clamped = EqDspMath.Clamp(db, minDb, maxDb);
            double t = (maxDb - clamped) / (maxDb - minDb);
            return plot.Top + (float)(t * plot.Height);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            Graphics g = e.Graphics;
            g.Clear(BackColor);

            Rectangle plot = GetPlotArea();
            float zeroY = DbToY(0.0, plot);

            using (SolidBrush whiteBrush = new SolidBrush(Color.White))
            {
                g.FillRectangle(whiteBrush, plot);
            }

            if (_eqEnabled && _peakWithPreampDb > 0.05)
            {
                Rectangle overZone = new Rectangle(plot.Left + 1, plot.Top + 1, plot.Width - 1, Math.Max(1, (int)Math.Round(zeroY - plot.Top - 1)));
                using (SolidBrush warnBg = new SolidBrush(Color.FromArgb(255, 241, 241)))
                {
                    g.FillRectangle(warnBg, overZone);
                }
            }

            int[] dbTicks = new int[] { 12, 6, 0, -6, -12 };
            using (Pen gridPen = new Pen(Color.FromArgb(232, 235, 240), 1f))
            using (Pen zeroPen = new Pen(Color.FromArgb(170, 178, 190), 1f))
            using (SolidBrush txtBrush = new SolidBrush(Color.FromArgb(90, 96, 108)))
            {
                for (int i = 0; i < dbTicks.Length; i++)
                {
                    int d = dbTicks[i];
                    float y = DbToY(d, plot);
                    g.DrawLine(d == 0 ? zeroPen : gridPen, plot.Left, y, plot.Right, y);
                    string lbl = d > 0 ? ("+" + d) : d.ToString(CultureInfo.InvariantCulture);
                    SizeF sz = g.MeasureString(lbl, _axisFont);
                    g.DrawString(lbl, _axisFont, txtBrush, plot.Left - sz.Width - UiScale.Px(4), y - sz.Height / 2f);
                }

                double[] freqTicks = new double[] { 20, 50, 100, 200, 500, 1000, 2000, 5000, 10000, 20000 };
                string[] freqLabels = new string[] { "20", "50", "100", "200", "500", "1k", "2k", "5k", "10k", "20k" };
                for (int i = 0; i < freqTicks.Length; i++)
                {
                    float x = FreqToX(freqTicks[i], plot);
                    g.DrawLine(gridPen, x, plot.Top, x, plot.Bottom);
                    SizeF sz = g.MeasureString(freqLabels[i], _axisFont);
                    float lx = x - sz.Width / 2f;
                    if (lx < plot.Left - UiScale.Px(6)) lx = plot.Left - UiScale.Px(6);
                    if (lx + sz.Width > ClientSize.Width - 2) lx = ClientSize.Width - sz.Width - 2;
                    g.DrawString(freqLabels[i], _axisFont, txtBrush, lx, plot.Bottom + UiScale.Px(2));
                }
            }

            float[][] coeffs = new float[11][];
            string err;
            for (int i = 0; i < 11; i++)
            {
                coeffs[i] = EqDspMath.ComputeBiquad(_profile.Bands[i], i + 1, out err);
                if (coeffs[i] == null) coeffs[i] = new float[] { 1f, 0f, 0f, 0f, 0f };
            }

            int samples = Math.Max(120, plot.Width / 2);
            PointF[] pts = new PointF[samples + 1];
            double[] dbs = new double[samples + 1];
            double logMin = Math.Log10(20.0);
            double logMax = Math.Log10(20000.0);
            for (int k = 0; k <= samples; k++)
            {
                double f = Math.Pow(10.0, logMin + (logMax - logMin) * k / samples);
                double db = _eqEnabled ? EqDspMath.EvaluateCascadeDbAtHz(coeffs, _profile.PreampDb, f) : 0.0;
                dbs[k] = db;
                pts[k] = new PointF(plot.Left + (float)k * plot.Width / samples, DbToY(db, plot));
            }

            g.SmoothingMode = SmoothingMode.AntiAlias;
            Region oldClip = g.Clip;
            g.SetClip(plot);
            try
            {
                Color safeLineColor = _eqEnabled ? Color.FromArgb(0, 108, 198) : Color.FromArgb(150, 156, 166);
                using (Pen safePen = new Pen(safeLineColor, UiScale.Pxf(2.0f)))
                using (Pen dangerPen = new Pen(Color.FromArgb(208, 32, 32), UiScale.Pxf(2.2f)))
                {
                    for (int k = 0; k < samples; k++)
                    {
                        bool over = _eqEnabled && (dbs[k] > 0.05 || dbs[k + 1] > 0.05);
                        g.DrawLine(over ? dangerPen : safePen, pts[k], pts[k + 1]);
                    }
                }

                for (int i = 0; i < 11; i++)
                {
                    _bandMarkerCenters[i] = new PointF(-100f, -100f);
                    EqBandSpec b = _profile.Bands[i];
                    if (!b.Enabled) continue;
                    double f = EqDspMath.Clamp(b.FrequencyHz, 20.0, 20000.0);
                    double db = _eqEnabled ? EqDspMath.EvaluateCascadeDbAtHz(coeffs, _profile.PreampDb, f) : 0.0;
                    float cx = FreqToX(f, plot);
                    float cy = DbToY(db, plot);
                    _bandMarkerCenters[i] = new PointF(cx, cy);

                    bool sel = (i == _selectedBand);
                    bool over = _eqEnabled && db > 0.05;
                    float r = UiScale.Pxf(sel ? 5.5f : 4.0f);
                    Color dotFill = over
                        ? Color.FromArgb(212, 36, 36)
                        : (sel ? Color.FromArgb(0, 86, 166) : Color.White);
                    Color dotBorder = over
                        ? Color.FromArgb(148, 16, 16)
                        : Color.FromArgb(0, 108, 198);

                    using (SolidBrush fb = new SolidBrush(dotFill))
                    using (Pen bp = new Pen(dotBorder, UiScale.Pxf(1.5f)))
                    {
                        g.FillEllipse(fb, cx - r, cy - r, r * 2f, r * 2f);
                        g.DrawEllipse(bp, cx - r, cy - r, r * 2f, r * 2f);
                    }
                }
            }
            finally
            {
                g.Clip = oldClip;
                g.SmoothingMode = SmoothingMode.Default;
            }

            using (Pen borderPen = new Pen(Color.FromArgb(160, 166, 176), 1f))
            {
                g.DrawRectangle(borderPen, plot.Left, plot.Top, plot.Width, plot.Height);
            }

            // Minimalist peak readout badge
            string badgeText;
            Color badgeFg;
            Color badgeBg;
            if (!_eqEnabled)
            {
                badgeText = "OFF";
                badgeFg = Color.FromArgb(100, 104, 112);
                badgeBg = Color.FromArgb(245, 246, 248);
            }
            else if (_peakWithPreampDb > 0.05)
            {
                badgeText = string.Format(CultureInfo.InvariantCulture, "ピーク: +{0:0.0} dB", _peakWithPreampDb);
                badgeFg = Color.FromArgb(168, 20, 20);
                badgeBg = Color.FromArgb(255, 230, 230);
            }
            else
            {
                badgeText = string.Format(CultureInfo.InvariantCulture, "ピーク: {0:+0.0;-0.0;0.0} dB", _peakWithPreampDb);
                badgeFg = Color.FromArgb(72, 78, 88);
                badgeBg = Color.FromArgb(245, 246, 248);
            }

            SizeF bsz = g.MeasureString(badgeText, _badgeFont);
            RectangleF badgeRect = new RectangleF(
                plot.Right - bsz.Width - UiScale.Px(12),
                plot.Top + UiScale.Px(4),
                bsz.Width + UiScale.Px(8),
                bsz.Height + UiScale.Px(2));
            using (SolidBrush bb = new SolidBrush(badgeBg))
            using (SolidBrush bf = new SolidBrush(badgeFg))
            using (Pen bp = new Pen(Color.FromArgb(196, 202, 210), 1f))
            {
                g.FillRectangle(bb, badgeRect);
                g.DrawRectangle(bp, badgeRect.X, badgeRect.Y, badgeRect.Width, badgeRect.Height);
                g.DrawString(badgeText, _badgeFont, bf, badgeRect.X + UiScale.Px(4), badgeRect.Y + UiScale.Px(1));
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (e.Button != MouseButtons.Left) return;
            float hitRadius = UiScale.Pxf(10f);
            int best = -1;
            float bestDistSq = hitRadius * hitRadius;
            for (int i = 0; i < 11; i++)
            {
                float dx = e.X - _bandMarkerCenters[i].X;
                float dy = e.Y - _bandMarkerCenters[i].Y;
                float d2 = dx * dx + dy * dy;
                if (d2 <= bestDistSq)
                {
                    bestDistSq = d2;
                    best = i;
                }
            }
            if (best >= 0)
            {
                SelectedBand = best;
                if (BandDotClicked != null) BandDotClicked(best);
            }
        }
    }

    internal sealed class MixerStatus
    {
        public bool ObsMicActive;
        public bool PcSpeakerActive;
        public bool SwitchI2sActive;
        public bool PcI2sActive;
        public bool UartLocked;
        public bool TestToneOn;
        public bool FlashConfigLoaded;
        public byte ActiveUartRxPin;
        public byte UartRxPinMode;
        public bool EqEnabled;
        public double MasterDb;
        public double GameDb;
        public double PcTrimDb;
        public double WindowsMasterVolDb;
        public bool WindowsMuted;
        public uint CrcErrors;
        public uint FlashSaves;
        public uint ClipCount;
    }

    internal sealed class HidDeviceConnection : IDisposable
    {
        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid g);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid g, string enumerator, IntPtr hwndParent, uint flags);

        [DllImport("setupapi.dll")]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData, IntPtr detailData, uint detailDataSize, out uint requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll")]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_SetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetFeature(SafeFileHandle hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDeviceInterfaceData
        {
            public uint cbSize;
            public Guid interfaceClassGuid;
            public uint flags;
            public UIntPtr reserved;
        }

        private readonly string _vidHex;
        private readonly string _pidHex;
        private SafeFileHandle _handle;

        public HidDeviceConnection(ushort vid, ushort pid)
        {
            _vidHex = vid.ToString("x4", CultureInfo.InvariantCulture);
            _pidHex = pid.ToString("x4", CultureInfo.InvariantCulture);
        }

        public string VidUpper { get { return _vidHex.ToUpperInvariant(); } }
        public string PidUpper { get { return _pidHex.ToUpperInvariant(); } }
        public bool IsConnected { get { return _handle != null && !_handle.IsInvalid && !_handle.IsClosed; } }

        public void CloseHandle()
        {
            if (_handle != null)
            {
                try { _handle.Dispose(); } catch { }
                _handle = null;
            }
        }

        public void Dispose()
        {
            CloseHandle();
        }

        private SafeFileHandle OpenMatchingDevice()
        {
            Guid hidGuid;
            HidD_GetHidGuid(out hidGuid);
            IntPtr set = SetupDiGetClassDevs(ref hidGuid, null, IntPtr.Zero, 0x12u);
            if (set == new IntPtr(-1))
            {
                throw new IOException("USB HIDデバイスの列挙に失敗しました。");
            }

            string needle = "vid_" + _vidHex + "&pid_" + _pidHex;
            SafeFileHandle chosen = null;
            try
            {
                for (uint idx = 0; ; idx++)
                {
                    SpDeviceInterfaceData iface = new SpDeviceInterfaceData();
                    iface.cbSize = (uint)Marshal.SizeOf(typeof(SpDeviceInterfaceData));
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, idx, ref iface))
                    {
                        break;
                    }

                    uint needed;
                    SetupDiGetDeviceInterfaceDetail(set, ref iface, IntPtr.Zero, 0, out needed, IntPtr.Zero);
                    if (needed < 8) continue;

                    IntPtr detail = Marshal.AllocHGlobal((int)needed);
                    try
                    {
                        Marshal.WriteInt32(detail, IntPtr.Size == 8 ? 8 : 6);
                        if (!SetupDiGetDeviceInterfaceDetail(set, ref iface, detail, needed, out needed, IntPtr.Zero))
                        {
                            continue;
                        }
                        string path = Marshal.PtrToStringUni(IntPtr.Add(detail, 4));
                        if (path == null || path.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0)
                        {
                            continue;
                        }

                        SafeFileHandle h = CreateFile(path, 0x80000000u, 3u, IntPtr.Zero, 3u, 0u, IntPtr.Zero);
                        if (h.IsInvalid)
                        {
                            h.Dispose();
                            continue;
                        }
                        if (chosen != null)
                        {
                            h.Dispose();
                            chosen.Dispose();
                            throw new IOException("複数の Biwamasu Mixer が同時に接続されています。");
                        }
                        chosen = h;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(detail);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }

            return chosen;
        }

        private SafeFileHandle EnsureHandle()
        {
            if (IsConnected) return _handle;
            _handle = OpenMatchingDevice();
            if (_handle == null)
            {
                throw new IOException(string.Format("Biwamasu Mixer (VID_{0} / PID_{1}) が未接続です。", VidUpper, PidUpper));
            }
            return _handle;
        }

        public byte[] Exchange(byte[] command, bool readBack)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                SafeFileHandle h = EnsureHandle();
                try
                {
                    if (command != null)
                    {
                        byte[] send = new byte[64];
                        int copyLen = Math.Min(command.Length, 63);
                        Array.Copy(command, 0, send, 1, copyLen);
                        if (!HidD_SetFeature(h, send, 64))
                        {
                            throw new IOException("HID書き込み失敗 (Code " + Marshal.GetLastWin32Error() + ")");
                        }
                    }

                    if (!readBack)
                    {
                        return new byte[64];
                    }

                    byte[] data = new byte[64];
                    if (!HidD_GetFeature(h, data, 64))
                    {
                        throw new IOException("HID読み取り失敗 (Code " + Marshal.GetLastWin32Error() + ")");
                    }
                    if (Encoding.ASCII.GetString(data, 1, 4) != "UAP1" || data[5] != 1)
                    {
                        throw new IOException("想定外のHID応答を受信しました。");
                    }
                    return data;
                }
                catch
                {
                    CloseHandle();
                    if (attempt == 1) throw;
                }
            }
            throw new IOException("HID通信に失敗しました。");
        }

        public byte[] ReadPage(byte page)
        {
            for (int retry = 0; retry < 3; retry++)
            {
                byte[] raw = Exchange(new byte[] { 0x10, page }, true);
                if (raw[6] == page)
                {
                    return raw;
                }
                Thread.Sleep(5);
            }
            throw new IOException("HIDページ番号不一致");
        }

        private static uint[] WordsFromPage(byte[] raw)
        {
            uint[] w = new uint[13];
            for (int i = 0; i < 13; i++)
            {
                w[i] = BitConverter.ToUInt32(raw, 9 + 4 * i);
            }
            return w;
        }

        private static int ToSigned32(uint u)
        {
            return BitConverter.ToInt32(BitConverter.GetBytes(u), 0);
        }

        public MixerStatus ReadFullStatus()
        {
            byte[] r0 = ReadPage(0);
            uint[] w0 = WordsFromPage(r0);
            byte[] r2 = ReadPage(2);
            uint[] w2 = WordsFromPage(r2);
            byte[] r3 = ReadPage(3);
            uint[] w3 = WordsFromPage(r3);

            byte flags = r0[7];
            MixerStatus s = new MixerStatus();
            s.ObsMicActive = (flags & 1) != 0;
            s.PcSpeakerActive = (flags & 2) != 0;
            s.SwitchI2sActive = (flags & 4) != 0;
            s.PcI2sActive = (flags & 8) != 0;
            s.UartLocked = (flags & 16) != 0;
            s.TestToneOn = (flags & 32) != 0;
            s.FlashConfigLoaded = (flags & 64) != 0;
            s.ActiveUartRxPin = r0[61];
            s.UartRxPinMode = r0[63];
            s.EqEnabled = w3[0] != 0;
            s.GameDb = Math.Round(ToSigned32(w3[1]) / 100.0, 1);
            s.MasterDb = Math.Round(ToSigned32(w3[2]) / 100.0, 1);
            s.PcTrimDb = Math.Round(ToSigned32(w3[3]) / 100.0, 1);
            s.WindowsMasterVolDb = Math.Round(ToSigned32(w3[4]) / 100.0, 1);
            s.WindowsMuted = ((w3[7] & 1u) != 0) || ((w3[7] & 6u) == 6u);
            s.CrcErrors = w0[4];
            s.FlashSaves = w3[10];
            s.ClipCount = w2[8];
            return s;
        }

        public void WriteParams(bool eqEnabled, double gameDb, double masterDb, double pcTrimDb, byte rxPinMode)
        {
            short gameX100 = ClampShort(Math.Round(gameDb * 100.0), -6000, 1200);
            short masterX100 = ClampShort(Math.Round(masterDb * 100.0), -6000, 600);
            short pcX100 = ClampShort(Math.Round(pcTrimDb * 100.0), -2400, 1200);
            byte pinByte = (rxPinMode == 0 || rxPinMode == 1 || rxPinMode == 13) ? rxPinMode : (byte)13;

            byte[] cmd = new byte[9];
            cmd[0] = 0x40;
            cmd[1] = eqEnabled ? (byte)1 : (byte)0;
            Array.Copy(BitConverter.GetBytes(gameX100), 0, cmd, 2, 2);
            Array.Copy(BitConverter.GetBytes(masterX100), 0, cmd, 4, 2);
            Array.Copy(BitConverter.GetBytes(pcX100), 0, cmd, 6, 2);
            cmd[8] = pinByte;
            Exchange(cmd, false);
        }

        public bool TryReadDeviceEq(out float preampLinear, out float[][] coeffs)
        {
            preampLinear = 1.0f;
            coeffs = null;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                try
                {
                    byte[] eqBytes = new byte[56 * 4];
                    uint expectedCrc = 0;
                    for (byte pg = 4; pg <= 8; pg++)
                    {
                        byte[] raw = ReadPage(pg);
                        uint[] w = WordsFromPage(raw);
                        int baseIdx = (pg - 4) * 12;
                        for (int k = 0; k < 12 && (baseIdx + k) < 56; k++)
                        {
                            byte[] wb = BitConverter.GetBytes(w[k]);
                            Array.Copy(wb, 0, eqBytes, (baseIdx + k) * 4, 4);
                        }
                        if (pg == 8) expectedCrc = w[12];
                    }

                    if (EqDspMath.ComputeCrc32(eqBytes) != expectedCrc)
                    {
                        continue;
                    }

                    float pre = BitConverter.ToSingle(eqBytes, 0);
                    if (float.IsNaN(pre) || float.IsInfinity(pre) || pre < 0.01f || pre > 4.0f)
                    {
                        return false;
                    }

                    float[][] parsed = new float[11][];
                    for (int b = 0; b < 11; b++)
                    {
                        float[] c = new float[5];
                        for (int j = 0; j < 5; j++)
                        {
                            c[j] = BitConverter.ToSingle(eqBytes, (1 + b * 5 + j) * 4);
                        }
                        if (!EqDspMath.BiquadCoeffStable(c)) return false;
                        parsed[b] = c;
                    }

                    preampLinear = pre;
                    coeffs = parsed;
                    return true;
                }
                catch
                {
                    // Older firmware only implements pages 0..3; ignore gracefully without dropping connection.
                    return false;
                }
            }
            return false;
        }

        public void WriteRawEqCoeffs(float preampLin, float[][] coeffs)
        {
            if (coeffs == null || coeffs.Length != 11) throw new ArgumentException("coeffs");
            byte[] pcmd = new byte[5];
            pcmd[0] = 0x43;
            Array.Copy(BitConverter.GetBytes(preampLin), 0, pcmd, 1, 4);

            // Begin atomic EQ staging on updated firmware; on older firmware 0x41/0x44 are no-ops
            // so sending attenuating preamp first prevents any momentary uncompensated boost.
            Exchange(new byte[] { 0x41, 0xA5 }, false);
            if (preampLin < 1.0f)
            {
                Exchange(pcmd, false);
            }

            for (int b = 0; b < 11; b++)
            {
                byte[] cmd = new byte[22];
                cmd[0] = 0x42;
                cmd[1] = (byte)b;
                for (int j = 0; j < 5; j++)
                {
                    Array.Copy(BitConverter.GetBytes(coeffs[b][j]), 0, cmd, 2 + j * 4, 4);
                }
                Exchange(cmd, false);
            }

            Exchange(pcmd, false);
            Exchange(new byte[] { 0x44, 0xA5 }, false);
        }

        public void WriteEqProfile(EqProfile profile)
        {
            string err;
            float preampLin = EqDspMath.ComputePreampLinear(profile.PreampDb, out err);
            if (err != null) throw new InvalidOperationException(err);

            float[][] coeffs = new float[11][];
            for (int i = 0; i < 11; i++)
            {
                coeffs[i] = EqDspMath.ComputeBiquad(profile.Bands[i], i + 1, out err);
                if (coeffs[i] == null) throw new InvalidOperationException(err);
            }

            WriteRawEqCoeffs(preampLin, coeffs);
        }

        public void SetTestTone(bool on)
        {
            Exchange(new byte[] { 0x20, on ? (byte)1 : (byte)0 }, false);
        }

        public void SaveToFlash()
        {
            Exchange(new byte[] { 0x50, 0xA5 }, false);
            Thread.Sleep(160);
        }

        public void ResetDefaults()
        {
            Exchange(new byte[] { 0x51, 0xA5 }, false);
            Thread.Sleep(40);
        }

        public void EnterBootsel()
        {
            Exchange(new byte[] { 0x7F, 0x00 }, false);
            CloseHandle();
        }

        private static short ClampShort(double v, int min, int max)
        {
            int iv = (int)v;
            if (iv < min) iv = min;
            if (iv > max) iv = max;
            return (short)iv;
        }
    }

    internal sealed class HelpGuideForm : Form
    {
        private sealed class HelpTopic
        {
            public string Title;
            public string Body;

            public HelpTopic(string title, string body)
            {
                Title = title;
                Body = body;
            }

            public override string ToString()
            {
                return Title;
            }
        }

        private readonly ListBox _topicList;
        private readonly Label _topicHeader;
        private readonly TextBox _bodyBox;
        private readonly HelpTopic[] _topics;

        public HelpGuideForm(Icon appIcon, int initialTopicIndex)
        {
            AutoScaleMode = AutoScaleMode.None;
            Font = UiScale.CreateUiFont();
            if (appIcon != null) Icon = appIcon;
            Text = "Biwamasu-Mixer Setting tool - ヘルプ・各項目ガイド";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(UiScale.Px(740), UiScale.Px(500));

            _topics = BuildTopics();

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = UiScale.Pad(12, 10, 12, 10);
            root.ColumnCount = 2;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiScale.Pxf(226f)));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowCount = 2;
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            GroupBox grpNav = new GroupBox();
            grpNav.Text = "項目一覧";
            grpNav.Dock = DockStyle.Fill;
            grpNav.Margin = UiScale.Pad(0, 0, 8, 8);
            grpNav.Padding = UiScale.Pad(6, 6, 6, 6);

            _topicList = new ListBox();
            _topicList.Dock = DockStyle.Fill;
            _topicList.IntegralHeight = false;
            for (int i = 0; i < _topics.Length; i++)
            {
                _topicList.Items.Add(_topics[i]);
            }
            _topicList.SelectedIndexChanged += OnTopicSelected;
            grpNav.Controls.Add(_topicList);

            GroupBox grpContent = new GroupBox();
            grpContent.Text = "ガイド内容";
            grpContent.Dock = DockStyle.Fill;
            grpContent.Margin = UiScale.Pad(0, 0, 0, 8);
            grpContent.Padding = UiScale.Pad(8, 6, 8, 8);

            TableLayoutPanel contentLayout = new TableLayoutPanel();
            contentLayout.Dock = DockStyle.Fill;
            contentLayout.ColumnCount = 1;
            contentLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            contentLayout.RowCount = 2;
            contentLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            contentLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            _topicHeader = new Label();
            _topicHeader.Font = new Font(Font.FontFamily, 10.2f, FontStyle.Bold, GraphicsUnit.Point);
            _topicHeader.AutoSize = true;
            _topicHeader.Margin = UiScale.Pad(2, 2, 2, 6);

            _bodyBox = new TextBox();
            _bodyBox.Multiline = true;
            _bodyBox.ReadOnly = true;
            _bodyBox.ScrollBars = ScrollBars.Vertical;
            _bodyBox.BackColor = SystemColors.Window;
            _bodyBox.ForeColor = SystemColors.WindowText;
            _bodyBox.Dock = DockStyle.Fill;
            _bodyBox.WordWrap = true;

            contentLayout.Controls.Add(_topicHeader, 0, 0);
            contentLayout.Controls.Add(_bodyBox, 0, 1);
            grpContent.Controls.Add(contentLayout);

            FlowLayoutPanel bottomBar = new FlowLayoutPanel();
            bottomBar.FlowDirection = FlowDirection.RightToLeft;
            bottomBar.Dock = DockStyle.Fill;
            bottomBar.AutoSize = true;
            bottomBar.Margin = new Padding(0);

            Button btnClose = UiScale.CreateButton("閉じる", 88, 27);
            btnClose.Margin = new Padding(0);
            btnClose.DialogResult = DialogResult.Cancel;
            bottomBar.Controls.Add(btnClose);
            CancelButton = btnClose;

            root.Controls.Add(grpNav, 0, 0);
            root.Controls.Add(grpContent, 1, 0);
            root.Controls.Add(bottomBar, 0, 1);
            root.SetColumnSpan(bottomBar, 2);
            Controls.Add(root);

            int idx = initialTopicIndex;
            if (idx < 0 || idx >= _topics.Length) idx = 0;
            _topicList.SelectedIndex = idx;
        }

        private void OnTopicSelected(object sender, EventArgs e)
        {
            int idx = _topicList.SelectedIndex;
            if (idx < 0 || idx >= _topics.Length) return;
            _topicHeader.Text = _topics[idx].Title;
            _bodyBox.Text = _topics[idx].Body;
            _bodyBox.SelectionStart = 0;
            _bodyBox.SelectionLength = 0;
            _bodyBox.ScrollToCaret();
        }

        private static HelpTopic[] BuildTopics()
        {
            return new HelpTopic[]
            {
                new HelpTopic(
                    "1. このツールの役割と基本手順",
                    "【このソフトは何をするもの？】\r\n" +
                    "「Biwamasu-Mixer Setting tool.exe」は、PC側のマイコン（Pico 2 / Biwamasu Mixer）内部の音量バランスやイコライザー設定を変更・保存するための設定ツールです。\r\n\r\n" +
                    "■ 常駐させる必要はありません\r\n" +
                    "音声のミックス処理やイコライザー処理はすべてマイコン基板の内部で行われています。\r\n" +
                    "一度好みの設定を作って右下の [本体に保存] を押せば、マイコン内部のフラッシュメモリに記憶されるため、普段はこのソフトを閉じてしまって構いません（PCを再起動したりUSBを抜き差ししても設定は自動で復元されます）。\r\n\r\n" +
                    "■ 安全な初回セットアップ手順\r\n" +
                    "1. 最初はイヤホン・ヘッドホンを耳に着けず、机の上に置いておきます（Switch 2本体の音量をMAXにして使う仕組みのため、設定前の爆音事故を防ぐためです）。\r\n" +
                    "2. イヤホンを Pico Audio Pack の「PHONES」と書いてある方の穴（隣の「LINE OUT」ではありません）に根元までしっかり挿します。\r\n" +
                    "3. Switch 2 の設定から「ヘッドホン バーチャルサラウンド」を ON、「ヘッドホンの最大音量を下げる」を OFF にし、Switch 2 本体の音量設定を MAX（最大）まで上げます。\r\n" +
                    "4. 本ツールの「マスター音量」を初期値（-45.0 dB）から少しずつ動かし、ちょうど良い音量を探します。\r\n" +
                    "5. 必要に応じてイコライザーを設定し、最後に [本体に保存] を押せば完了です。"),

                new HelpTopic(
                    "2. メイン画面：接続状態欄",
                    "■ 状態: 接続済み / 未接続\r\n" +
                    "PCと Biwamasu Mixer（Pico②）がUSB接続され、通信できているかを示します。\r\n" +
                    "「未接続」になる場合は、充電専用ではないデータ通信対応の Micro USB ケーブルで PC に接続されているか確認してください。\r\n\r\n" +
                    "■ [Windowsミュート中] 表示\r\n" +
                    "Windows側のサウンド設定で「ヘッドホン (Biwamasu Mixer)」がミュートになっている場合に表示されます。PCの音だけ鳴らないときの確認に役立ちます。\r\n\r\n" +
                    "■ Switch 2: 受信中 / 待機中\r\n" +
                    "Switch 2 側のマイコン（Pico①）から 3Mbps デジタル音声データが届いているかを示します。\r\n" +
                    "・受信中：正常にデジタル音声リンクが確立しています。\r\n" +
                    "・待機中：Switch 2 がスリープ中・未接続、または Switch 2 側の出力先が「Biwamasu Mixer TX」になっていない、もしくはブレッドボード配線（GP0→33Ω→GP13、GND共通線）が抜けています。\r\n\r\n" +
                    "■ OBS: 動作中 / 待機中\r\n" +
                    "OBS Studio 等の録音・配信ソフトが「ライン (Biwamasu Mixer)」を開いて音声を取り込んでいるときに「動作中」になります。\r\n\r\n" +
                    "■ [音割れ検知] 表示\r\n" +
                    "普段は何も表示されませんが、再生中にマイコン内部でデジタル上限（0 dBFS）を超える音割れ（クリッピング）が実際に発生したときだけ警告が表示されます。表示された場合は、EQ詳細設定で [自動調整] を押してプリアンプを下げるか、音量補正・マスター音量を下げてください。\r\n\r\n" +
                    "■ [再読込] ボタン\r\n" +
                    "通常は2秒おきに自動更新されますが、ボタンを押すと即座にマイコンの現在値を読み直します。"),

                new HelpTopic(
                    "3. 音量バランスと赤色ゾーン",
                    "■ マスター音量（-60.0 〜 +6.0 dB / 初期値 -45.0 dB）\r\n" +
                    "ヘッドホン端子から出力される最終的な全体音量です（Switch 2音・PC音の両方に効きます）。\r\n" +
                    "スライダーをダブルクリックすると安全な初期値（-45.0 dB）に戻ります。\r\n\r\n" +
                    "■ Switch 2 補正（-60.0 〜 +12.0 dB / 初期値 0.0 dB）\r\n" +
                    "Switch 2 のゲーム音だけの音量を前後させます。スライダーをダブルクリックすると 0.0 dB に戻ります。\r\n" +
                    "（※OBS録音用のライン入力には影響せず、OBSへは常に劣化なしの生音がそのまま送られます）\r\n\r\n" +
                    "■ PC 音量補正（-24.0 〜 +12.0 dB / 初期値 0.0 dB）\r\n" +
                    "PC側の音（Discordや動画など）の基準音量を前後させます（ダブルクリックで 0.0 dB）。\r\n" +
                    "普段のPC音量の上げ下げは、Windows右下の標準音量スライダーでもそのまま操作できます。\r\n\r\n" +
                    "■ スライダーの赤色ゾーン（合計 +0 dB 超過・非推奨）とは？\r\n" +
                    "「マスター音量 ＋ Switch 2補正」または「マスター音量 ＋ PC音量補正」の合計が +0.0 dB を超える区間は、スライダーの溝が連動して赤く表示されます。\r\n" +
                    "（例：マスター音量が -11 dB のときは補正 +11 dB 以上が赤、マスター音量が 0 dB のときは補正 0 dB 以上がすべて赤になります）\r\n\r\n" +
                    "【なぜ赤色ゾーンが非推奨なのか？】\r\n" +
                    "・デジタル音声の最大値（0 dBFS）を超えてプラス増幅するため、大きな効果音で天井にぶつかり「音割れ（クリッピング）」が発生します。\r\n" +
                    "・微小な背景ノイズまで一緒に増幅されるため、S/N比が悪化（サーッというノイズが増加）します。\r\n\r\n" +
                    "【音量が足りないときの正しい対処法】\r\n" +
                    "バランス調整は「大きすぎる方をマイナスに下げる」形で行い、合計 0 dB 以下を保ってください。\r\n" +
                    "マスター音量を 0 dB まで上げてもヘッドホンの音量が小さい場合は、デジタルでプラス増幅するのではなく、いったんヘッドホンを耳から外してマスター音量を下げてから、Pico Audio Pack 基板上にある小さなスイッチを「LO GAIN」から「HI GAIN」へ切り替えて再調整してください。"),

                new HelpTopic(
                    "4. イコライザー欄と下部ボタン",
                    "■ イコライザー [有効にする]\r\n" +
                    "マイコン内蔵の最大11バンド・パラメトリックEQの ON / OFF を切り替えます。\r\n" +
                    "Switch 2音とPC音がミックスされた後の音声（自分の耳に聴こえる音）に適用されます。OBSへ送られる録音用音声には適用されません。\r\n" +
                    "※マイナスのプリアンプを設定している場合、EQをOFFにするとプリアンプによる減衰も解除されるため、その分だけ音量が上がります（切り替え自体は滑らかにフェードします）。\r\n" +
                    "※注意：PC側の Equalizer APO（Peace）等のEQソフトが「ヘッドホン (Biwamasu Mixer)」にも掛かっていると、PC音にだけ二重でEQが掛かってしまいます。PC側のEQソフトをOFFにするか、「Biwamasu Mixer」にはEQが掛からない設定にしておいてください（Device Selector 等で他のヘッドホンにだけ掛かる設定になっていればそのままで大丈夫です）。\r\n\r\n" +
                    "■ [ファイル読込...] / [AI・テキスト貼付...] / [詳細設定...]\r\n" +
                    "・[ファイル読込...]：Peace の「.peace」ファイルや Equalizer APO / AutoEQ 形式の「.txt」ファイルを読み込んでマイコンへ反映します（ファイルをウィンドウへ直接ドラッグ＆ドロップしても読み込めます）。\r\n" +
                    "・[AI・テキスト貼付...]：AIへの依頼文（プロンプト）を1クリックでコピーしたり、AIが出力したテキストをメモ帳保存なしでそのまま貼り付けて適用できます（メイン画面で Ctrl+V でも貼り付け可能です）。\r\n" +
                    "・[詳細設定...]：11バンドの周波数特性グラフを見ながら細かく調整する専用ウィンドウを開きます。\r\n\r\n" +
                    "■ [テスト音: OFF / ON]\r\n" +
                    "マイコン内部で生成した確認用電子音（左耳 440Hz / 右耳 660Hz）を鳴らします。Windowsの設定に関係なくマイコンから直接鳴るため、配線チェックや左右チャンネル確認に使えます。\r\n\r\n" +
                    "■ [初期値に戻す]\r\n" +
                    "マスター音量 -45.0 dB、各補正 0.0 dB、イコライザー OFF の初期状態へ戻します。\r\n\r\n" +
                    "■ [本体に保存]（未保存の変更があるときは「*」が付きます）\r\n" +
                    "現在の設定を Pico② 本体のフラッシュメモリに保存します。\r\n" +
                    "スライダーを動かしただけでは一時反映（電源を切ると戻る状態）なので、設定が決まったら必ず [本体に保存] を押してください。"),

                new HelpTopic(
                    "5. EQ詳細設定：全体設定とグラフ",
                    "■ [有効] / [リアルタイム反映]\r\n" +
                    "「リアルタイム反映」にチェックを入れておくと、スライダーや数値を動かした瞬間にミキサーの音へ反映されるため、Switch 2 や YouTube の音を流しながら耳で聴いて調整できます。\r\n" +
                    "もし調整前の音に戻したくなった場合は、右下の [キャンセル] を押して閉じれば、詳細画面を開く前の状態へ自動的に復元されます。\r\n\r\n" +
                    "■ プリアンプ（-36.0 〜 +12.0 dB）と [自動調整] ボタン\r\n" +
                    "イコライザーで特定の音域をプラス（＋）に持ち上げると、その帯域だけ音量が 0 dB の上限を超えて音割れしやすくなります。それを防ぐために全体の音量をあらかじめ引き算する項目が「プリアンプ」です。\r\n" +
                    "横にある [自動調整] ボタンを押すと、現在のグラフの最大ピークがぴったり 0.0 dB 以下に収まる安全なマイナスプリアンプ値が自動で入力されます。\r\n\r\n" +
                    "■ プリセット選択と [読込]\r\n" +
                    "・標準 (100Hz LSC - PK - 10kHz HSC)：低音シェルフ＋中間ピーキング9枠＋高音シェルフの定番構成\r\n" +
                    "・グラフィックEQ風 (31.5Hz - 16kHz)：一般的な10バンドスライダー式EQと同じ固定周波数構成\r\n" +
                    "・低音ブースト / ボイス・足音重視：調整の参考用サンプル\r\n" +
                    "・フラット (0 dB)：現在の周波数配置を残したまま、すべてのゲインを 0.0 dB に戻します\r\n\r\n" +
                    "■ 周波数特性グラフ\r\n" +
                    "横軸が音の高さ（左が低音 20Hz 〜 右が高音 20kHz）、縦軸が音量の増減（dB）です。\r\n" +
                    "合計が 0 dB を超えている音割れ注意区間は線が赤色で描かれます（[自動調整] を押すと解消します）。グラフ上の丸印をクリックすると、対応するバンド行にフォーカスが移ります。"),

                new HelpTopic(
                    "6. EQ詳細設定：種類・周波数・Q値",
                    "■ 有効チェックボックス\r\n" +
                    "各バンド（Band 1 〜 Band 11）を個別に ON / OFF できます。数値を残したまま特定のバンドの効果だけを聴き比べたいときに便利です。\r\n\r\n" +
                    "■ 種類（フィルターの形）\r\n" +
                    "・PK (ピーキング)：指定した周波数の周辺だけを山のように持ち上げたり、谷のように削ったりする基本フィルターです。\r\n" +
                    "・LSC (ローシェルフ)：指定した周波数より「下の低音全体」をまとめて上下させます。100 Hz 前後（Q = 0.71）に設定して低音全体の迫力やボワつきを整えるのが定番です。\r\n" +
                    "・HSC (ハイシェルフ)：指定した周波数より「上の高音全体」をまとめて上下させます。10000 Hz 前後（Q = 0.71）に設定して音の抜け感や耳への刺さりを整えるのが定番です。\r\n\r\n" +
                    "■ 周波数 (Hz)（10 〜 23500 Hz）\r\n" +
                    "調整したい音の高さを直接手入力します（Enter キーまたは別の欄をクリックすると確定します）。\r\n" +
                    "【音域の目安】\r\n" +
                    "・60 〜 120 Hz：重低音・爆発音などの迫力\r\n" +
                    "・200 〜 400 Hz：音の厚み（上げすぎるとこもりの原因になりやすい帯域）\r\n" +
                    "・1000 〜 3000 Hz：人の声の聞き取りやすさ・足音や銃声の芯\r\n" +
                    "・4000 〜 8000 Hz：音の輪郭・歯擦音（サ行の刺さり）\r\n" +
                    "・10000 Hz 以上：空気感・キラびやかさ\r\n\r\n" +
                    "■ ゲイン / dB（スライダー -15〜+15 dB / 手入力 -30〜+24 dB）\r\n" +
                    "持ち上げる量（＋）または削る量（−）です。スライダーをダブルクリックすると 0.0 dB に戻ります。\r\n\r\n" +
                    "■ Q（効く幅の鋭さ：0.06 〜 30.0）\r\n" +
                    "・小さい値（0.5 〜 0.8）：広い範囲になだらかに効きます（LSC / HSC シェルフでは 0.71 が標準）。\r\n" +
                    "・中間の値（1.0 〜 2.0）：一般的なイコライザーに近い自然な山・谷になります（PK では 1.41 が標準）。\r\n" +
                    "・大きい値（3.0 〜 8.0以上）：非常に狭い範囲だけをピンポイントで鋭く動かします（特定の耳障りなピークだけを狙って削るときなどに使います）。"),

                new HelpTopic(
                    "7. PeaceやAutoEQ、AIでのEQ読込",
                    "■ Equalizer APO（Peace）を使っている場合\r\n" +
                    "Peace 画面下部にある「エクスポート」ボタンは押さないでください（Peace のエクスポート機能は Audacity 用の別形式を出力するため読み込めません）。\r\n\r\n" +
                    "代わりに、本ツールの [ファイル読込...] ボタンを押すと、自動的に以下のフォルダが開きます：\r\n" +
                    "  C:\\Program Files\\EqualizerAPO\\config\r\n" +
                    "このフォルダの中にある「<プリセット名>.peace」ファイル、または「peace.txt」をそのまま選ぶだけで、プリアンプ値と各バンド設定がそのまま読み込まれます。\r\n\r\n" +
                    "■ AutoEQ（Webサイト）からイヤホン・ヘッドホン補正を読み込む場合\r\n" +
                    "公開データベースの AutoEQ（https://autoeq.app）には、多数の市販イヤホン・ヘッドホンの測定済み補正データが公開されており、無変換でそのまま読み込めます。\r\n" +
                    "1. https://autoeq.app を開き、検索欄から手持ちのイヤホン・ヘッドホン機種名を選びます。\r\n" +
                    "2. アプリ選択欄（Select equalizer app）で「EqualizerAPO ParametricEq」を選びます。\r\n" +
                    "3. ダウンロードボタンを押して保存した「.txt」ファイルを、本ツールの [ファイル読込...] から開くか、ウィンドウへそのままドラッグ＆ドロップするだけで反映されます（10バンド構成・音割れ防止プリアンプ計算済み）。\r\n\r\n" +
                    "■ AI にイコライザーを作ってもらう場合（ファイル保存不要）\r\n" +
                    "1. 本ツールの [AI・テキスト貼付...] ボタンを押します。\r\n" +
                    "2. 必要に応じて機種名や好みの音を入力し、[AIへの依頼文をコピー] を押して ChatGPT / Claude / Gemini 等に貼り付けます（他ソフトのEQ画面のスクショを一緒に添付してもOKです）。\r\n" +
                    "3. AIが出力した回答をコピーし、同じ画面のテキスト欄に貼り付けて（またはメイン画面で Ctrl+V を押して）[読み込んで適用] を押すだけで反映されます（前後の会話文や ``` 記号が混ざっていても自動でフィルター行だけを抽出します）。\r\n\r\n" +
                    "■ 10バンド以下のスライダー式EQを手動で再現したい場合\r\n" +
                    "詳細画面のプリセットから「グラフィックEQ風 (31.5Hz - 16kHz)」を選ぶと、一般的な10バンドEQと同じ周波数配置になるため、各スライダーの dB 値をそのまま入力できます。"),

                new HelpTopic(
                    "8. メニュー機能・困ったときは",
                    "■ お気に入り メニュー（音量バランス＋EQのセット保存・切替）\r\n" +
                    "複数のイヤホン・ヘッドホンやゲームごとの設定を使い分けたいときは、上部メニューの「お気に入り」→「現在の設定をお気に入りに追加」（Ctrl+D）から名前を付けて保存できます。\r\n" +
                    "マスター音量・各音量補正・EQ設定が丸ごとセットでPC内に保存され、次回からは「お気に入り」メニューから選ぶだけで1クリックで一括切替できます（電源切断後もその設定にしたい場合は切替後に [本体に保存] を押してください）。\r\n\r\n" +
                    "■ ツール → Windows サウンド設定を開く\r\n" +
                    "Windows標準のサウンドコントロールパネル（mmsys.cpl）を開きます。\r\n" +
                    "・再生タブ：「ヘッドホン (Biwamasu Mixer)」を既定のデバイスに設定します。\r\n" +
                    "・録音タブ：OBSで取り込む「ライン (Biwamasu Mixer)」のレベルが「100」（0.0 dB 等倍）、かつ「オーディオの強化」がオフになっているか確認できます。\r\n\r\n" +
                    "■ ツール → BOOTSELモードへ切替\r\n" +
                    "基板の白いボタンを押さずに、PC側の Pico② をファームウェア更新モード（USBドライブが開く状態）へ切り替えます。新しい biwamasu-mixer-rx.uf2 を書き込むとき以外は使わなくて大丈夫です。\r\n\r\n" +
                    "■ よくある質問・トラブル対処\r\n" +
                    "Q. 片耳からしか音が出ない／音がすごく遠い\r\n" +
                    "A. 新品の Pico Audio Pack のイヤホン穴は少し硬めです。プラグの根元まで「カチッ」と奥まで差し込まれているか確認してください。また、隣の「LINE OUT」ではなく「PHONES」と書いてある方の穴に挿してください。\r\n\r\n" +
                    "Q. 設定を変えたのに、USBを抜き差ししたら元に戻った\r\n" +
                    "A. メイン画面右下の [本体に保存] ボタンを押すとマイコン内部に記憶されます。\r\n\r\n" +
                    "Q. 名前の由来は？\r\n" +
                    "A. 制作者の好きな食べ物(ビワマス)が由来です。")
            };
        }
    }

    internal sealed class FavoritePreset
    {
        public string Name = "お気に入り";
        public string FilePath;
        public double MasterDb = -45.0;
        public double GameDb = 0.0;
        public double PcTrimDb = 0.0;
        public bool EqEnabled = false;
        public EqProfile Eq = new EqProfile();

        public string FormatMenuText()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "{0}  (Master {1:0.0} dB / EQ {2})",
                Name,
                MasterDb,
                EqEnabled ? "ON" : "OFF");
        }

        public string FormatDetailText()
        {
            return string.Format(
                CultureInfo.InvariantCulture,
                "Master {0:0.0} dB  |  Switch 2 {1:+0.0;-0.0;0.0} dB  |  PC {2:+0.0;-0.0;0.0} dB  |  EQ {3} ({4}バンド, Preamp {5:+0.0;-0.0;0.0} dB)",
                MasterDb,
                GameDb,
                PcTrimDb,
                EqEnabled ? "ON" : "OFF",
                Eq != null ? Eq.ActiveBandCount() : 0,
                Eq != null ? Eq.PreampDb : 0.0);
        }

        public override string ToString()
        {
            return FormatMenuText();
        }
    }

    internal sealed class FavoritePresetStore
    {
        private static readonly Regex MetaRx = new Regex(
            @"^\s*#\s*([A-Za-z0-9_]+)\s*:\s*(.+?)\s*$",
            RegexOptions.Compiled);

        private readonly string _dir;

        public FavoritePresetStore(string directoryPath)
        {
            _dir = directoryPath;
        }

        public static string GetDefaultDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BiwamasuMixer",
                "favorites");
        }

        public List<FavoritePreset> ListAll()
        {
            List<FavoritePreset> list = new List<FavoritePreset>();
            if (string.IsNullOrEmpty(_dir) || !Directory.Exists(_dir)) return list;

            string[] files;
            try { files = Directory.GetFiles(_dir, "*.txt"); }
            catch { return list; }

            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    FavoritePreset fp = LoadFile(files[i]);
                    if (fp != null) list.Add(fp);
                }
                catch { }
            }

            list.Sort(delegate(FavoritePreset a, FavoritePreset b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
            return list;
        }

        public FavoritePreset FindByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            string trimmed = name.Trim();
            List<FavoritePreset> all = ListAll();
            for (int i = 0; i < all.Count; i++)
            {
                if (string.Equals(all[i].Name, trimmed, StringComparison.CurrentCultureIgnoreCase))
                {
                    return all[i];
                }
            }
            return null;
        }

        public FavoritePreset Save(FavoritePreset preset)
        {
            if (preset == null) throw new ArgumentNullException("preset");
            string cleanName = (preset.Name ?? "").Trim();
            if (cleanName.Length == 0) throw new InvalidOperationException("名前を入力してください。");
            if (!Directory.Exists(_dir)) Directory.CreateDirectory(_dir);

            string targetPath = preset.FilePath;
            if (string.IsNullOrEmpty(targetPath) || !File.Exists(targetPath))
            {
                FavoritePreset existing = FindByName(cleanName);
                if (existing != null && !string.IsNullOrEmpty(existing.FilePath))
                {
                    targetPath = existing.FilePath;
                }
                else
                {
                    targetPath = BuildUniqueFilePath(cleanName);
                }
            }

            EqProfile eq = preset.Eq != null ? preset.Eq.Clone() : new EqProfile();
            eq.SourceLabel = cleanName;

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("# BiwamasuFavorite: 1");
            sb.AppendLine("# Preset: " + cleanName);
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "# MasterDb: {0:0.0}", preset.MasterDb));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "# GameDb: {0:0.0}", preset.GameDb));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "# PcTrimDb: {0:0.0}", preset.PcTrimDb));
            sb.AppendLine("# EqEnabled: " + (preset.EqEnabled ? "1" : "0"));
            sb.AppendLine(string.Format(CultureInfo.InvariantCulture, "Preamp: {0:0.0} dB", eq.PreampDb));
            for (int i = 0; i < 11; i++)
            {
                EqBandSpec b = eq.Bands[i];
                string state = b.Enabled ? "ON" : "OFF";
                string kind = b.Kind == FilterKind.PK ? "PK" : (b.Kind == FilterKind.LSC ? "LSC" : "HSC");
                string fcStr = EqDspMath.FormatFrequencyClean(b.FrequencyHz);
                sb.AppendLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "Filter {0}: {1} {2} Fc {3} Hz Gain {4:0.0} dB Q {5:0.00}",
                    i + 1, state, kind, fcStr, b.GainDb, b.Q));
            }

            File.WriteAllText(targetPath, sb.ToString(), new UTF8Encoding(false));
            preset.Name = cleanName;
            preset.FilePath = targetPath;
            preset.Eq = eq;
            return preset;
        }

        public void Delete(FavoritePreset preset)
        {
            if (preset == null || string.IsNullOrEmpty(preset.FilePath)) return;
            if (File.Exists(preset.FilePath))
            {
                File.Delete(preset.FilePath);
            }
        }

        private string BuildUniqueFilePath(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            StringBuilder safe = new StringBuilder();
            for (int i = 0; i < name.Length; i++)
            {
                char ch = name[i];
                if (Array.IndexOf(invalid, ch) >= 0 || ch == '.' || ch == ' ')
                {
                    safe.Append('_');
                }
                else
                {
                    safe.Append(ch);
                }
            }
            string stem = safe.ToString().Trim('_');
            if (stem.Length == 0) stem = "favorite";
            if (stem.Length > 40) stem = stem.Substring(0, 40);
            if (Regex.IsMatch(stem, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$", RegexOptions.IgnoreCase))
            {
                stem = "_" + stem;
            }

            string candidate = Path.Combine(_dir, stem + ".txt");
            int seq = 2;
            while (File.Exists(candidate))
            {
                candidate = Path.Combine(_dir, stem + "_" + seq + ".txt");
                seq++;
            }
            return candidate;
        }

        private static FavoritePreset LoadFile(string path)
        {
            string raw = File.ReadAllText(path, Encoding.UTF8);
            string[] lines = raw.Split(new string[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

            FavoritePreset fp = new FavoritePreset();
            fp.FilePath = path;
            fp.Name = Path.GetFileNameWithoutExtension(path);

            for (int i = 0; i < lines.Length; i++)
            {
                Match m = MetaRx.Match(lines[i]);
                if (!m.Success) continue;
                string k = m.Groups[1].Value;
                string v = m.Groups[2].Value.Trim();
                double dv;
                if (string.Equals(k, "Preset", StringComparison.OrdinalIgnoreCase) && v.Length > 0)
                {
                    fp.Name = v;
                }
                else if (string.Equals(k, "MasterDb", StringComparison.OrdinalIgnoreCase) &&
                         EqDspMath.TryParseStrictNumber(v, out dv))
                {
                    fp.MasterDb = EqDspMath.Clamp(dv, -60.0, 6.0);
                }
                else if (string.Equals(k, "GameDb", StringComparison.OrdinalIgnoreCase) &&
                         EqDspMath.TryParseStrictNumber(v, out dv))
                {
                    fp.GameDb = EqDspMath.Clamp(dv, -60.0, 12.0);
                }
                else if (string.Equals(k, "PcTrimDb", StringComparison.OrdinalIgnoreCase) &&
                         EqDspMath.TryParseStrictNumber(v, out dv))
                {
                    fp.PcTrimDb = EqDspMath.Clamp(dv, -24.0, 12.0);
                }
                else if (string.Equals(k, "EqEnabled", StringComparison.OrdinalIgnoreCase))
                {
                    fp.EqEnabled = (v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));
                }
            }

            fp.Eq = EqDspMath.ParsePeaceText(raw, fp.Name);
            fp.Eq.SourceLabel = fp.Name;
            return fp;
        }
    }

    internal sealed class AiEqPasteForm : Form
    {
        private readonly Icon _appIcon;
        private readonly bool _autoPrefillOnShown;
        private readonly TextBox _txtDevice;
        private readonly TextBox _txtGoal;
        private readonly Label _lblCopyInfo;
        private readonly TextBox _txtPaste;
        private EqProfile _parsedProfile;

        public EqProfile ParsedProfile { get { return _parsedProfile; } }

        public AiEqPasteForm(Icon appIcon) : this(appIcon, true) { }

        public AiEqPasteForm(Icon appIcon, bool autoPrefillOnShown)
        {
            _appIcon = appIcon;
            _autoPrefillOnShown = autoPrefillOnShown;
            AutoScaleMode = AutoScaleMode.None;
            Font = UiScale.CreateUiFont();
            if (appIcon != null) Icon = appIcon;
            Text = "AI作成・テキスト貼付によるEQ読込";
            KeyPreview = true;
            AllowDrop = true;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(UiScale.Px(620), UiScale.Px(476));

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = UiScale.Pad(12, 8, 12, 10);
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // 1. AI prompt helper
            GroupBox grpPrompt = new GroupBox();
            grpPrompt.Text = "1. AIでEQを作る場合（依頼文の作成・コピー）";
            grpPrompt.Dock = DockStyle.Fill;
            grpPrompt.AutoSize = true;
            grpPrompt.Margin = UiScale.Pad(0, 0, 0, 8);
            grpPrompt.Padding = UiScale.Pad(10, 6, 10, 8);

            TableLayoutPanel pGrid = new TableLayoutPanel();
            pGrid.Dock = DockStyle.Fill;
            pGrid.AutoSize = true;
            pGrid.ColumnCount = 2;
            pGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiScale.Pxf(156f)));
            pGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            pGrid.RowCount = 3;

            Label lblDev = new Label();
            lblDev.Text = "使用機種名 (任意):";
            lblDev.AutoSize = true;
            lblDev.Anchor = AnchorStyles.Left;
            lblDev.Margin = UiScale.Pad(0, 4, 6, 4);

            _txtDevice = new TextBox();
            _txtDevice.Dock = DockStyle.Fill;
            _txtDevice.Margin = UiScale.Pad(0, 2, 0, 4);

            Label lblGoal = new Label();
            lblGoal.Text = "音の好み・要望 (任意):";
            lblGoal.AutoSize = true;
            lblGoal.Anchor = AnchorStyles.Left;
            lblGoal.Margin = UiScale.Pad(0, 4, 6, 4);

            _txtGoal = new TextBox();
            _txtGoal.Dock = DockStyle.Fill;
            _txtGoal.Margin = UiScale.Pad(0, 2, 0, 4);

            TableLayoutPanel copyRow = new TableLayoutPanel();
            copyRow.Dock = DockStyle.Fill;
            copyRow.AutoSize = true;
            copyRow.ColumnCount = 2;
            copyRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            copyRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            copyRow.Margin = UiScale.Pad(0, 2, 0, 0);

            _lblCopyInfo = new Label();
            _lblCopyInfo.Text = "※空欄のままコピーして ChatGPT / Claude / Gemini 側で書き足してもOKです";
            _lblCopyInfo.ForeColor = SystemColors.ControlDarkDark;
            _lblCopyInfo.AutoSize = true;
            _lblCopyInfo.Anchor = AnchorStyles.Left;
            _lblCopyInfo.Margin = new Padding(0);

            Button btnCopyPrompt = UiScale.CreateButton("AIへの依頼文をコピー(&C)", 172, 26);
            btnCopyPrompt.Margin = new Padding(0);
            btnCopyPrompt.Click += OnCopyPromptClick;

            copyRow.Controls.Add(_lblCopyInfo, 0, 0);
            copyRow.Controls.Add(btnCopyPrompt, 1, 0);

            pGrid.Controls.Add(lblDev, 0, 0);
            pGrid.Controls.Add(_txtDevice, 1, 0);
            pGrid.Controls.Add(lblGoal, 0, 1);
            pGrid.Controls.Add(_txtGoal, 1, 1);
            pGrid.Controls.Add(copyRow, 0, 2);
            pGrid.SetColumnSpan(copyRow, 2);
            grpPrompt.Controls.Add(pGrid);

            // 2. Paste box
            GroupBox grpPaste = new GroupBox();
            grpPaste.Text = "2. EQテキストを貼り付けて適用（AIの回答全文そのままでもOK）";
            grpPaste.Dock = DockStyle.Fill;
            grpPaste.Margin = UiScale.Pad(0, 0, 0, 8);
            grpPaste.Padding = UiScale.Pad(10, 6, 10, 8);

            TableLayoutPanel pasteLayout = new TableLayoutPanel();
            pasteLayout.Dock = DockStyle.Fill;
            pasteLayout.ColumnCount = 1;
            pasteLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            pasteLayout.RowCount = 2;
            pasteLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            pasteLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            TableLayoutPanel pasteHeader = new TableLayoutPanel();
            pasteHeader.Dock = DockStyle.Fill;
            pasteHeader.AutoSize = true;
            pasteHeader.ColumnCount = 2;
            pasteHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            pasteHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            pasteHeader.Margin = UiScale.Pad(0, 0, 0, 4);

            Label lblPasteHint = new Label();
            lblPasteHint.Text = "Preamp: / Filter 1: ... を含むテキストを貼り付けてください:";
            lblPasteHint.AutoSize = true;
            lblPasteHint.Anchor = AnchorStyles.Left;
            lblPasteHint.Margin = new Padding(0);

            Button btnPasteClip = UiScale.CreateButton("クリップボードから貼付(&V)", 168, 25);
            btnPasteClip.Margin = new Padding(0);
            btnPasteClip.Click += OnPasteClipboardClick;

            pasteHeader.Controls.Add(lblPasteHint, 0, 0);
            pasteHeader.Controls.Add(btnPasteClip, 1, 0);

            _txtPaste = new TextBox();
            _txtPaste.Multiline = true;
            _txtPaste.AcceptsReturn = true;
            _txtPaste.WordWrap = false;
            _txtPaste.ScrollBars = ScrollBars.Both;
            _txtPaste.Dock = DockStyle.Fill;
            try { _txtPaste.Font = new Font("Consolas", 9.2f, FontStyle.Regular, GraphicsUnit.Point); }
            catch { }

            pasteLayout.Controls.Add(pasteHeader, 0, 0);
            pasteLayout.Controls.Add(_txtPaste, 0, 1);
            grpPaste.Controls.Add(pasteLayout);

            // 3. Bottom buttons
            TableLayoutPanel bottomBar = new TableLayoutPanel();
            bottomBar.Dock = DockStyle.Fill;
            bottomBar.AutoSize = true;
            bottomBar.ColumnCount = 2;
            bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottomBar.Margin = new Padding(0);

            Button btnHelp = UiScale.CreateButton("ヘルプ(&H)...", 96, 27);
            btnHelp.Margin = new Padding(0);
            btnHelp.Click += delegate
            {
                using (HelpGuideForm hf = new HelpGuideForm(_appIcon, 6))
                {
                    hf.ShowDialog(this);
                }
            };

            FlowLayoutPanel rightBtns = new FlowLayoutPanel();
            rightBtns.FlowDirection = FlowDirection.RightToLeft;
            rightBtns.Dock = DockStyle.Fill;
            rightBtns.AutoSize = true;
            rightBtns.WrapContents = false;
            rightBtns.Margin = new Padding(0);

            Button btnCancel = UiScale.CreateButton("キャンセル", 92, 27);
            btnCancel.Margin = UiScale.Pad(6, 0, 0, 0);
            btnCancel.DialogResult = DialogResult.Cancel;

            Button btnImport = UiScale.CreateButton("読み込んで適用", 128, 27);
            btnImport.Margin = new Padding(0);
            btnImport.Click += OnImportClick;

            rightBtns.Controls.Add(btnCancel);
            rightBtns.Controls.Add(btnImport);

            bottomBar.Controls.Add(btnHelp, 0, 0);
            bottomBar.Controls.Add(rightBtns, 1, 0);

            CancelButton = btnCancel;

            root.Controls.Add(grpPrompt, 0, 0);
            root.Controls.Add(grpPaste, 0, 1);
            root.Controls.Add(bottomBar, 0, 2);
            Controls.Add(root);

            DragEnter += OnFormDragEnter;
            DragDrop += OnFormDragDrop;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (_autoPrefillOnShown)
            {
                TryAutoPrefillFromClipboard();
            }
        }

        private void TryAutoPrefillFromClipboard()
        {
            try
            {
                if (!Clipboard.ContainsText()) return;
                string clip = Clipboard.GetText();
                if (string.IsNullOrEmpty(clip) || clip.Length > 200000) return;
                EqProfile test = EqDspMath.ParsePeaceText(clip, "AI・テキスト貼付");
                if (test != null)
                {
                    _txtPaste.Text = clip;
                    _txtPaste.SelectionStart = 0;
                    _txtPaste.SelectionLength = 0;
                    _lblCopyInfo.ForeColor = Color.FromArgb(16, 108, 48);
                    _lblCopyInfo.Text = "クリップボード内のEQデータを自動入力しました。[読み込んで適用] を押してください。";
                }
            }
            catch { }
        }

        private void OnCopyPromptClick(object sender, EventArgs e)
        {
            try
            {
                string prompt = EqDspMath.BuildAiEqPrompt(_txtDevice.Text, _txtGoal.Text);
                Clipboard.SetText(prompt);
                _lblCopyInfo.ForeColor = Color.FromArgb(16, 108, 48);
                _lblCopyInfo.Text = "依頼文をコピーしました！ChatGPT / Claude / Gemini 等に貼り付けてください。";
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "クリップボードへのコピーに失敗しました: " + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnPasteClipboardClick(object sender, EventArgs e)
        {
            try
            {
                if (!Clipboard.ContainsText())
                {
                    MessageBox.Show(this, "クリップボードにテキストがありません。", "貼り付け", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                _txtPaste.Text = Clipboard.GetText();
                _txtPaste.Focus();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "貼り付けに失敗しました: " + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnImportClick(object sender, EventArgs e)
        {
            try
            {
                string fallbackLabel = _txtDevice.Text != null && _txtDevice.Text.Trim().Length > 0
                    ? _txtDevice.Text.Trim()
                    : "AI・テキスト貼付";
                _parsedProfile = EqDspMath.ParsePeaceText(_txtPaste.Text, fallbackLabel);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "テキスト読込エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void OnFormDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                e.Effect = DragDropEffects.Copy;
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        private void OnFormDragDrop(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null && files.Length > 0 && File.Exists(files[0]))
                    {
                        _txtPaste.Text = File.ReadAllText(files[0], Encoding.UTF8);
                        return;
                    }
                }
                if (e.Data.GetDataPresent(DataFormats.UnicodeText))
                {
                    string txt = e.Data.GetData(DataFormats.UnicodeText) as string;
                    if (!string.IsNullOrEmpty(txt)) _txtPaste.Text = txt;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F1)
            {
                e.Handled = true;
                using (HelpGuideForm hf = new HelpGuideForm(_appIcon, 6))
                {
                    hf.ShowDialog(this);
                }
                return;
            }
            base.OnKeyDown(e);
        }
    }

    internal sealed class PromptNameDialog : Form
    {
        private readonly TextBox _txtName;

        public string EnteredName { get { return _txtName.Text != null ? _txtName.Text.Trim() : ""; } }

        public PromptNameDialog(Icon appIcon, string windowTitle, string promptLabel, string initialValue, string detailSummary)
        {
            AutoScaleMode = AutoScaleMode.None;
            Font = UiScale.CreateUiFont();
            if (appIcon != null) Icon = appIcon;
            Text = windowTitle;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(UiScale.Px(430), UiScale.Px(148));

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = UiScale.Pad(12, 10, 12, 10);
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowCount = 4;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            Label lbl = new Label();
            lbl.Text = promptLabel;
            lbl.AutoSize = true;
            lbl.Margin = UiScale.Pad(0, 0, 0, 4);

            _txtName = new TextBox();
            _txtName.Dock = DockStyle.Fill;
            _txtName.MaxLength = 48;
            _txtName.Text = initialValue ?? "";
            _txtName.Margin = UiScale.Pad(0, 0, 0, 6);

            Label lblSub = new Label();
            lblSub.Text = detailSummary ?? "";
            lblSub.ForeColor = SystemColors.ControlDarkDark;
            lblSub.AutoSize = true;
            lblSub.Margin = new Padding(0);

            FlowLayoutPanel btns = new FlowLayoutPanel();
            btns.FlowDirection = FlowDirection.RightToLeft;
            btns.Dock = DockStyle.Fill;
            btns.AutoSize = true;
            btns.WrapContents = false;
            btns.Margin = new Padding(0);

            Button btnCancel = UiScale.CreateButton("キャンセル", 88, 26);
            btnCancel.Margin = UiScale.Pad(6, 0, 0, 0);
            btnCancel.DialogResult = DialogResult.Cancel;

            Button btnOk = UiScale.CreateButton("保存", 88, 26);
            btnOk.Margin = new Padding(0);
            btnOk.Click += delegate
            {
                if (EnteredName.Length == 0)
                {
                    MessageBox.Show(this, "名前を入力してください。", "入力確認", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    _txtName.Focus();
                    return;
                }
                DialogResult = DialogResult.OK;
                Close();
            };

            btns.Controls.Add(btnCancel);
            btns.Controls.Add(btnOk);

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            root.Controls.Add(lbl, 0, 0);
            root.Controls.Add(_txtName, 0, 1);
            root.Controls.Add(lblSub, 0, 2);
            root.Controls.Add(btns, 0, 3);
            Controls.Add(root);
        }
    }

    internal sealed class FavoriteManagerForm : Form
    {
        private readonly Icon _appIcon;
        private readonly FavoritePresetStore _store;
        private readonly Func<FavoritePreset> _captureCurrentCallback;
        private readonly Action<FavoritePreset> _applyCallback;
        private readonly ListBox _listBox;
        private readonly Label _lblDetail;
        private List<FavoritePreset> _items = new List<FavoritePreset>();

        public FavoriteManagerForm(
            Icon appIcon,
            FavoritePresetStore store,
            Func<FavoritePreset> captureCurrentCallback,
            Action<FavoritePreset> applyCallback)
        {
            _appIcon = appIcon;
            _store = store;
            _captureCurrentCallback = captureCurrentCallback;
            _applyCallback = applyCallback;

            AutoScaleMode = AutoScaleMode.None;
            Font = UiScale.CreateUiFont();
            if (appIcon != null) Icon = appIcon;
            Text = "お気に入り設定の管理（音量バランス＋EQセット）";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(UiScale.Px(560), UiScale.Px(330));

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = UiScale.Pad(12, 10, 12, 10);
            root.ColumnCount = 2;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            root.RowCount = 3;
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            _listBox = new ListBox();
            _listBox.Dock = DockStyle.Fill;
            _listBox.IntegralHeight = false;
            _listBox.Margin = UiScale.Pad(0, 0, 10, 6);
            _listBox.SelectedIndexChanged += delegate { UpdateDetailLabel(); };
            _listBox.DoubleClick += OnApplySelectedClick;

            FlowLayoutPanel sideBtns = new FlowLayoutPanel();
            sideBtns.FlowDirection = FlowDirection.TopDown;
            sideBtns.AutoSize = true;
            sideBtns.WrapContents = false;
            sideBtns.Margin = new Padding(0);

            Button btnApply = UiScale.CreateButton("適用(&A)", 138, 27);
            btnApply.Margin = UiScale.Pad(0, 0, 0, 6);
            btnApply.Click += OnApplySelectedClick;

            Button btnAdd = UiScale.CreateButton("現在値を新規追加(&N)...", 138, 27);
            btnAdd.Margin = UiScale.Pad(0, 0, 0, 6);
            btnAdd.Click += OnAddCurrentClick;

            Button btnOverwrite = UiScale.CreateButton("現在値で上書き(&U)", 138, 27);
            btnOverwrite.Margin = UiScale.Pad(0, 0, 0, 6);
            btnOverwrite.Click += OnOverwriteSelectedClick;

            Button btnRename = UiScale.CreateButton("名前変更(&R)...", 138, 27);
            btnRename.Margin = UiScale.Pad(0, 0, 0, 6);
            btnRename.Click += OnRenameSelectedClick;

            Button btnDelete = UiScale.CreateButton("削除(&D)", 138, 27);
            btnDelete.Margin = new Padding(0);
            btnDelete.Click += OnDeleteSelectedClick;

            sideBtns.Controls.Add(btnApply);
            sideBtns.Controls.Add(btnAdd);
            sideBtns.Controls.Add(btnOverwrite);
            sideBtns.Controls.Add(btnRename);
            sideBtns.Controls.Add(btnDelete);

            _lblDetail = new Label();
            _lblDetail.ForeColor = SystemColors.ControlDarkDark;
            _lblDetail.AutoSize = true;
            _lblDetail.Margin = UiScale.Pad(0, 2, 0, 8);

            FlowLayoutPanel bottomBar = new FlowLayoutPanel();
            bottomBar.FlowDirection = FlowDirection.RightToLeft;
            bottomBar.Dock = DockStyle.Fill;
            bottomBar.AutoSize = true;
            bottomBar.Margin = new Padding(0);

            Button btnClose = UiScale.CreateButton("閉じる", 92, 27);
            btnClose.Margin = new Padding(0);
            btnClose.DialogResult = DialogResult.Cancel;
            bottomBar.Controls.Add(btnClose);
            CancelButton = btnClose;

            root.Controls.Add(_listBox, 0, 0);
            root.Controls.Add(sideBtns, 1, 0);
            root.Controls.Add(_lblDetail, 0, 1);
            root.SetColumnSpan(_lblDetail, 2);
            root.Controls.Add(bottomBar, 0, 2);
            root.SetColumnSpan(bottomBar, 2);
            Controls.Add(root);

            ReloadList(null);
        }

        private FavoritePreset SelectedPreset
        {
            get
            {
                int idx = _listBox.SelectedIndex;
                if (idx < 0 || idx >= _items.Count) return null;
                return _items[idx];
            }
        }

        private void ReloadList(string selectName)
        {
            _items = _store.ListAll();
            _listBox.Items.Clear();
            int selIdx = -1;
            for (int i = 0; i < _items.Count; i++)
            {
                _listBox.Items.Add(_items[i]);
                if (!string.IsNullOrEmpty(selectName) &&
                    string.Equals(_items[i].Name, selectName, StringComparison.CurrentCultureIgnoreCase))
                {
                    selIdx = i;
                }
            }
            if (selIdx < 0 && _items.Count > 0) selIdx = 0;
            if (selIdx >= 0) _listBox.SelectedIndex = selIdx;
            UpdateDetailLabel();
        }

        private void UpdateDetailLabel()
        {
            FavoritePreset fp = SelectedPreset;
            if (fp == null)
            {
                _lblDetail.Text = _items.Count == 0
                    ? "まだお気に入りが登録されていません。[現在値を新規追加...] から保存できます。"
                    : "項目を選択してください（ダブルクリックで即座に適用できます）。";
            }
            else
            {
                _lblDetail.Text = fp.FormatDetailText();
            }
        }

        private void OnApplySelectedClick(object sender, EventArgs e)
        {
            FavoritePreset fp = SelectedPreset;
            if (fp == null) return;
            if (_applyCallback != null) _applyCallback(fp);
        }

        private void OnAddCurrentClick(object sender, EventArgs e)
        {
            if (_captureCurrentCallback == null) return;
            FavoritePreset snap = _captureCurrentCallback();
            using (PromptNameDialog dlg = new PromptNameDialog(
                _appIcon,
                "お気に入りに追加",
                "保存する名前（イヤホン・ヘッドホン名や用途など）:",
                snap.Name,
                snap.FormatDetailText()))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                FavoritePreset existing = _store.FindByName(dlg.EnteredName);
                if (existing != null)
                {
                    DialogResult ans = MessageBox.Show(
                        this,
                        "「" + dlg.EnteredName + "」は既に存在します。上書きしますか？",
                        "上書き確認",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);
                    if (ans != DialogResult.Yes) return;
                    snap.FilePath = existing.FilePath;
                }
                snap.Name = dlg.EnteredName;
                try
                {
                    _store.Save(snap);
                    ReloadList(snap.Name);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "お気に入りの保存に失敗しました: " + ex.Message, "保存エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void OnOverwriteSelectedClick(object sender, EventArgs e)
        {
            FavoritePreset sel = SelectedPreset;
            if (sel == null || _captureCurrentCallback == null) return;
            DialogResult ans = MessageBox.Show(
                this,
                "「" + sel.Name + "」を現在の音量・EQ設定で上書きしますか？",
                "上書き確認",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (ans != DialogResult.Yes) return;

            FavoritePreset snap = _captureCurrentCallback();
            snap.Name = sel.Name;
            snap.FilePath = sel.FilePath;
            try
            {
                _store.Save(snap);
                ReloadList(snap.Name);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "お気に入りの上書き保存に失敗しました: " + ex.Message, "保存エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnRenameSelectedClick(object sender, EventArgs e)
        {
            FavoritePreset sel = SelectedPreset;
            if (sel == null) return;
            using (PromptNameDialog dlg = new PromptNameDialog(
                _appIcon,
                "お気に入りの名前変更",
                "新しい名前:",
                sel.Name,
                sel.FormatDetailText()))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                if (string.Equals(sel.Name, dlg.EnteredName, StringComparison.CurrentCulture)) return;
                FavoritePreset existing = _store.FindByName(dlg.EnteredName);
                if (existing != null && existing.FilePath != sel.FilePath)
                {
                    MessageBox.Show(this, "同じ名前のお気に入りが既に存在します。", "名前重複", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                string prevName = sel.Name;
                sel.Name = dlg.EnteredName;
                try
                {
                    _store.Save(sel);
                    ReloadList(sel.Name);
                }
                catch (Exception ex)
                {
                    sel.Name = prevName;
                    MessageBox.Show(this, "名前の変更に失敗しました: " + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void OnDeleteSelectedClick(object sender, EventArgs e)
        {
            FavoritePreset sel = SelectedPreset;
            if (sel == null) return;
            DialogResult ans = MessageBox.Show(
                this,
                "「" + sel.Name + "」を削除しますか？",
                "削除確認",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (ans != DialogResult.Yes) return;
            try
            {
                _store.Delete(sel);
                ReloadList(null);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "削除に失敗しました: " + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    internal sealed class EqEditorForm : Form
    {
        private readonly EqProfile _initialSnapshot;
        private bool _initialEqEnabled;
        private float _initialHwPreamp;
        private float[][] _initialHwCoeffs;
        private bool _hasRawHwRestore;
        private readonly EqProfile _working;
        private readonly Action<EqProfile, bool, bool> _applyCallback;
        private readonly Action<float, float[][], EqProfile, bool> _restoreRawCallback;
        private readonly Icon _appIcon;
        private string _currentEqDir;

        private readonly CheckBox _chkEqEnable;
        private readonly CheckBox _chkLivePreview;
        private readonly TextBox _txtPreamp;
        private readonly Button _btnAutoPreamp;
        private readonly ComboBox _cmbTemplate;
        private readonly EqResponseGraph _graph;
        private readonly CheckBox[] _rowEnable = new CheckBox[11];
        private readonly ComboBox[] _rowKind = new ComboBox[11];
        private readonly TextBox[] _rowFreq = new TextBox[11];
        private readonly GainSlider[] _rowGainSlider = new GainSlider[11];
        private readonly TextBox[] _rowGainText = new TextBox[11];
        private readonly TextBox[] _rowQ = new TextBox[11];
        private readonly System.Windows.Forms.Timer _liveSendTimer;

        private bool _suppressEvents;
        private bool _livePreviewDirtiedHardware;
        private bool _confirmedExit;

        public EqEditorForm(
            EqProfile current,
            bool eqEnabled,
            string initialEqDir,
            Icon appIcon,
            Action<EqProfile, bool, bool> applyCallback)
            : this(current, eqEnabled, initialEqDir, appIcon, applyCallback, 1.0f, null, null)
        {
        }

        public EqEditorForm(
            EqProfile current,
            bool eqEnabled,
            string initialEqDir,
            Icon appIcon,
            Action<EqProfile, bool, bool> applyCallback,
            float initialHwPreamp,
            float[][] initialHwCoeffs,
            Action<float, float[][], EqProfile, bool> restoreRawCallback)
        {
            _initialSnapshot = current.Clone();
            _initialEqEnabled = eqEnabled;
            _initialHwPreamp = initialHwPreamp;
            _initialHwCoeffs = CloneCoeffs(initialHwCoeffs);
            _hasRawHwRestore = (_initialHwCoeffs != null && restoreRawCallback != null);
            _working = current.Clone();
            _currentEqDir = initialEqDir;
            _appIcon = appIcon;
            _applyCallback = applyCallback;
            _restoreRawCallback = restoreRawCallback;

            AutoScaleMode = AutoScaleMode.None;
            Font = UiScale.CreateUiFont();
            if (appIcon != null) Icon = appIcon;
            Text = "イコライザー詳細設定";
            KeyPreview = true;
            AllowDrop = true;
            FormBorderStyle = FormBorderStyle.Sizable;
            MaximizeBox = true;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(UiScale.Px(680), UiScale.Px(420));
            ClientSize = new Size(UiScale.Px(780), UiScale.Px(604));

            _liveSendTimer = new System.Windows.Forms.Timer();
            _liveSendTimer.Interval = 45;
            _liveSendTimer.Tick += OnLiveSendTimerTick;

            TableLayoutPanel root = new TableLayoutPanel();
            root.Dock = DockStyle.Fill;
            root.Padding = UiScale.Pad(12, 8, 12, 10);
            root.ColumnCount = 1;
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            root.RowCount = 4;
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiScale.Pxf(128f)));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
            root.RowStyles.Add(new RowStyle(SizeType.AutoSize));

            // 1. Top settings
            GroupBox grpTop = new GroupBox();
            grpTop.Text = "全体設定";
            grpTop.Dock = DockStyle.Fill;
            grpTop.AutoSize = true;
            grpTop.Margin = UiScale.Pad(0, 0, 0, 6);
            grpTop.Padding = UiScale.Pad(10, 5, 10, 6);

            TableLayoutPanel topRows = new TableLayoutPanel();
            topRows.Dock = DockStyle.Fill;
            topRows.AutoSize = true;
            topRows.ColumnCount = 2;
            topRows.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            topRows.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            topRows.RowCount = 2;

            FlowLayoutPanel topLeft0 = new FlowLayoutPanel();
            topLeft0.AutoSize = true;
            topLeft0.WrapContents = false;
            topLeft0.Anchor = AnchorStyles.Left;
            topLeft0.Margin = new Padding(0);

            _chkEqEnable = new CheckBox();
            _chkEqEnable.Text = "有効(&E)";
            _chkEqEnable.Checked = eqEnabled;
            _chkEqEnable.AutoSize = true;
            _chkEqEnable.Margin = UiScale.Pad(0, 3, 18, 2);
            _chkEqEnable.CheckedChanged += delegate
            {
                if (_suppressEvents) return;
                RefreshGraphAndHeadroomUi();
                QueueLivePreview();
            };

            _chkLivePreview = new CheckBox();
            _chkLivePreview.Text = "リアルタイム反映";
            _chkLivePreview.Checked = true;
            _chkLivePreview.AutoSize = true;
            _chkLivePreview.Margin = UiScale.Pad(0, 3, 0, 2);

            topLeft0.Controls.Add(_chkEqEnable);
            topLeft0.Controls.Add(_chkLivePreview);

            FlowLayoutPanel topRight0 = new FlowLayoutPanel();
            topRight0.AutoSize = true;
            topRight0.WrapContents = false;
            topRight0.Anchor = AnchorStyles.Right;
            topRight0.Margin = new Padding(0);

            Button btnLoad = UiScale.CreateButton("ファイル読込(&L)...", 116, 25);
            btnLoad.Click += OnLoadPeaceClick;
            Button btnPasteAi = UiScale.CreateButton("AI・テキスト貼付(&P)...", 138, 25);
            btnPasteAi.Click += OnOpenAiPasteClick;
            Button btnExport = UiScale.CreateButton("ファイル保存(&S)...", 116, 25);
            btnExport.Margin = UiScale.Pad(0, 2, 0, 2);
            btnExport.Click += OnExportPeaceClick;
            topRight0.Controls.Add(btnLoad);
            topRight0.Controls.Add(btnPasteAi);
            topRight0.Controls.Add(btnExport);

            FlowLayoutPanel topLeft1 = new FlowLayoutPanel();
            topLeft1.AutoSize = true;
            topLeft1.WrapContents = false;
            topLeft1.Anchor = AnchorStyles.Left;
            topLeft1.Margin = UiScale.Pad(0, 3, 0, 0);

            Label lblPre = new Label();
            lblPre.Text = "プリアンプ:";
            lblPre.AutoSize = true;
            lblPre.Margin = UiScale.Pad(0, 5, 4, 0);

            _txtPreamp = new TextBox();
            _txtPreamp.Width = UiScale.Px(54);
            _txtPreamp.TextAlign = HorizontalAlignment.Right;
            _txtPreamp.Margin = UiScale.Pad(0, 2, 4, 0);
            _txtPreamp.Leave += delegate { CommitPreampText(); };
            _txtPreamp.KeyDown += delegate(object s, KeyEventArgs ke)
            {
                if (ke.KeyCode == Keys.Enter)
                {
                    ke.SuppressKeyPress = true;
                    CommitPreampText();
                }
            };

            Label lblPreUnit = new Label();
            lblPreUnit.Text = "dB";
            lblPreUnit.AutoSize = true;
            lblPreUnit.Margin = UiScale.Pad(0, 5, 10, 0);

            _btnAutoPreamp = UiScale.CreateButton("自動調整", 122, 25);
            _btnAutoPreamp.Margin = UiScale.Pad(0, 1, 0, 0);
            _btnAutoPreamp.Click += OnAutoPreampClick;

            topLeft1.Controls.Add(lblPre);
            topLeft1.Controls.Add(_txtPreamp);
            topLeft1.Controls.Add(lblPreUnit);
            topLeft1.Controls.Add(_btnAutoPreamp);

            FlowLayoutPanel topRight1 = new FlowLayoutPanel();
            topRight1.AutoSize = true;
            topRight1.WrapContents = false;
            topRight1.Anchor = AnchorStyles.Right;
            topRight1.Margin = UiScale.Pad(0, 3, 0, 0);

            Label lblTpl = new Label();
            lblTpl.Text = "プリセット:";
            lblTpl.AutoSize = true;
            lblTpl.Margin = UiScale.Pad(0, 5, 4, 0);

            _cmbTemplate = new ComboBox();
            _cmbTemplate.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbTemplate.Width = UiScale.Px(220);
            _cmbTemplate.Margin = UiScale.Pad(0, 2, 6, 0);
            _cmbTemplate.Items.AddRange(new object[]
            {
                "標準 (100Hz LSC - PK - 10kHz HSC)",
                "グラフィックEQ風 (31.5Hz - 16kHz)",
                "低音ブースト",
                "ボイス・足音重視",
                "フラット (0 dB)"
            });
            _cmbTemplate.SelectedIndex = 0;

            Button btnApplyTpl = UiScale.CreateButton("読込", 56, 25);
            btnApplyTpl.Margin = UiScale.Pad(0, 1, 0, 0);
            btnApplyTpl.Click += OnApplyTemplateClick;

            topRight1.Controls.Add(lblTpl);
            topRight1.Controls.Add(_cmbTemplate);
            topRight1.Controls.Add(btnApplyTpl);

            topRows.Controls.Add(topLeft0, 0, 0);
            topRows.Controls.Add(topRight0, 1, 0);
            topRows.Controls.Add(topLeft1, 0, 1);
            topRows.Controls.Add(topRight1, 1, 1);
            grpTop.Controls.Add(topRows);

            // 2. Frequency Response Curve
            GroupBox grpGraph = new GroupBox();
            grpGraph.Text = "周波数特性";
            grpGraph.Dock = DockStyle.Fill;
            grpGraph.Margin = UiScale.Pad(0, 0, 0, 6);
            grpGraph.Padding = UiScale.Pad(8, 4, 8, 6);

            _graph = new EqResponseGraph();
            _graph.Dock = DockStyle.Fill;
            _graph.BandDotClicked += delegate(int idx)
            {
                if (idx >= 0 && idx < 11 && _rowGainSlider[idx].Enabled)
                {
                    _rowGainSlider[idx].Focus();
                }
            };
            grpGraph.Controls.Add(_graph);

            // 3. Band table
            GroupBox grpBands = new GroupBox();
            grpBands.Text = "バンド設定";
            grpBands.Dock = DockStyle.Fill;
            grpBands.Margin = UiScale.Pad(0, 0, 0, 8);
            grpBands.Padding = UiScale.Pad(10, 5, 10, 6);

            TableLayoutPanel grid = new TableLayoutPanel();
            grid.Dock = DockStyle.Fill;
            grid.ColumnCount = 7;
            grid.RowCount = 12;
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiScale.Pxf(64f)));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiScale.Pxf(42f)));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiScale.Pxf(168f)));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiScale.Pxf(84f)));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiScale.Pxf(70f)));
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiScale.Pxf(64f)));

            grid.RowStyles.Add(new RowStyle(SizeType.Absolute, UiScale.Pxf(22f)));
            AddHeaderLabel(grid, "バンド", 0);
            AddHeaderLabel(grid, "有効", 1);
            AddHeaderLabel(grid, "種類", 2);
            AddHeaderLabel(grid, "周波数 (Hz)", 3);
            AddHeaderLabel(grid, "ゲイン", 4);
            AddHeaderLabel(grid, "dB", 5);
            AddHeaderLabel(grid, "Q", 6);

            for (int i = 0; i < 11; i++)
            {
                grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100f / 11f));
                int row = i + 1;
                int bandIdx = i;

                Label lblBand = new Label();
                lblBand.Text = string.Format("Band {0}", row);
                lblBand.AutoSize = true;
                lblBand.Anchor = AnchorStyles.Left;
                grid.Controls.Add(lblBand, 0, row);

                CheckBox chk = new CheckBox();
                chk.AutoSize = true;
                chk.Anchor = AnchorStyles.Left;
                chk.Margin = UiScale.Pad(8, 1, 0, 1);
                chk.CheckedChanged += delegate { OnRowEnableChanged(bandIdx); };
                _rowEnable[i] = chk;
                grid.Controls.Add(chk, 1, row);

                ComboBox cmb = new ComboBox();
                cmb.DropDownStyle = ComboBoxStyle.DropDownList;
                cmb.Items.AddRange(new object[]
                {
                    "PK (ピーキング)",
                    "LSC (ローシェルフ)",
                    "HSC (ハイシェルフ)"
                });
                cmb.Dock = DockStyle.Fill;
                cmb.Margin = UiScale.Pad(2, 1, 8, 1);
                cmb.Enter += delegate { _graph.SelectedBand = bandIdx; };
                cmb.SelectedIndexChanged += delegate { OnRowKindChanged(bandIdx); };
                _rowKind[i] = cmb;
                grid.Controls.Add(cmb, 2, row);

                TextBox txtFc = new TextBox();
                txtFc.TextAlign = HorizontalAlignment.Right;
                txtFc.Dock = DockStyle.Fill;
                txtFc.Margin = UiScale.Pad(2, 1, 8, 1);
                txtFc.Enter += delegate { _graph.SelectedBand = bandIdx; };
                txtFc.Leave += delegate { CommitRowFreq(bandIdx); };
                txtFc.KeyDown += delegate(object s, KeyEventArgs ke)
                {
                    if (ke.KeyCode == Keys.Enter)
                    {
                        ke.SuppressKeyPress = true;
                        CommitRowFreq(bandIdx);
                    }
                };
                _rowFreq[i] = txtFc;
                grid.Controls.Add(txtFc, 3, row);

                GainSlider sld = new GainSlider();
                sld.MinimumDb = -15.0;
                sld.MaximumDb = 15.0;
                sld.DefaultResetDb = 0.0;
                sld.BipolarFromZero = true;
                sld.ShowScaleLabels = false;
                sld.ScaleTicks = null;
                sld.Dock = DockStyle.Fill;
                sld.Margin = UiScale.Pad(2, 1, 6, 1);
                sld.Enter += delegate { _graph.SelectedBand = bandIdx; };
                sld.ValueChanged += delegate { OnRowSliderChanged(bandIdx); };
                _rowGainSlider[i] = sld;
                grid.Controls.Add(sld, 4, row);

                TextBox txtGain = new TextBox();
                txtGain.TextAlign = HorizontalAlignment.Right;
                txtGain.Dock = DockStyle.Fill;
                txtGain.Margin = UiScale.Pad(2, 1, 8, 1);
                txtGain.Enter += delegate { _graph.SelectedBand = bandIdx; };
                txtGain.Leave += delegate { CommitRowGainText(bandIdx); };
                txtGain.KeyDown += delegate(object s, KeyEventArgs ke)
                {
                    if (ke.KeyCode == Keys.Enter)
                    {
                        ke.SuppressKeyPress = true;
                        CommitRowGainText(bandIdx);
                    }
                };
                _rowGainText[i] = txtGain;
                grid.Controls.Add(txtGain, 5, row);

                TextBox txtQ = new TextBox();
                txtQ.TextAlign = HorizontalAlignment.Right;
                txtQ.Dock = DockStyle.Fill;
                txtQ.Margin = UiScale.Pad(2, 1, 2, 1);
                txtQ.Enter += delegate { _graph.SelectedBand = bandIdx; };
                txtQ.Leave += delegate { CommitRowQ(bandIdx); };
                txtQ.KeyDown += delegate(object s, KeyEventArgs ke)
                {
                    if (ke.KeyCode == Keys.Enter)
                    {
                        ke.SuppressKeyPress = true;
                        CommitRowQ(bandIdx);
                    }
                };
                _rowQ[i] = txtQ;
                grid.Controls.Add(txtQ, 6, row);
            }
            grid.MinimumSize = new Size(0, UiScale.Px(308));
            Panel bandScroll = new Panel();
            bandScroll.Dock = DockStyle.Fill;
            bandScroll.AutoScroll = true;
            bandScroll.Controls.Add(grid);
            grpBands.Controls.Add(bandScroll);

            // 4. Standard dialog buttons (Help on left, OK/Cancel/Apply on right)
            TableLayoutPanel bottomBar = new TableLayoutPanel();
            bottomBar.Dock = DockStyle.Fill;
            bottomBar.AutoSize = true;
            bottomBar.ColumnCount = 2;
            bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottomBar.Margin = new Padding(0);

            Button btnHelp = UiScale.CreateButton("ヘルプ(&H)...", 92, 27);
            btnHelp.Margin = UiScale.Pad(0, 2, 0, 2);
            btnHelp.Click += delegate { OpenEqHelp(); };

            FlowLayoutPanel bottomBtns = new FlowLayoutPanel();
            bottomBtns.FlowDirection = FlowDirection.RightToLeft;
            bottomBtns.Dock = DockStyle.Fill;
            bottomBtns.AutoSize = true;
            bottomBtns.Margin = new Padding(0);

            Button btnApply = UiScale.CreateButton("適用(&A)", 88, 27);
            btnApply.Margin = UiScale.Pad(6, 2, 0, 2);
            btnApply.Click += OnApplyClick;
            Button btnCancel = UiScale.CreateButton("キャンセル", 88, 27);
            btnCancel.Margin = UiScale.Pad(6, 2, 0, 2);
            btnCancel.DialogResult = DialogResult.Cancel;
            Button btnOk = UiScale.CreateButton("OK", 88, 27);
            btnOk.Margin = UiScale.Pad(0, 2, 0, 2);
            btnOk.Click += OnOkClick;

            bottomBtns.Controls.Add(btnApply);
            bottomBtns.Controls.Add(btnCancel);
            bottomBtns.Controls.Add(btnOk);

            bottomBar.Controls.Add(btnHelp, 0, 0);
            bottomBar.Controls.Add(bottomBtns, 1, 0);

            AcceptButton = btnOk;
            CancelButton = btnCancel;

            root.Controls.Add(grpTop, 0, 0);
            root.Controls.Add(grpGraph, 0, 1);
            root.Controls.Add(grpBands, 0, 2);
            root.Controls.Add(bottomBar, 0, 3);
            Controls.Add(root);

            DragEnter += OnEqDragEnter;
            DragDrop += OnEqDragDrop;

            PopulateControlsFromModel(_working);
        }

        private void OpenEqHelp()
        {
            using (HelpGuideForm hf = new HelpGuideForm(_appIcon, 4))
            {
                hf.ShowDialog(this);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.F1)
            {
                e.Handled = true;
                OpenEqHelp();
                return;
            }
            if (e.Control && e.KeyCode == Keys.V && (e.Shift || !(ActiveControl is TextBox)))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                TryPasteOrOpenAiDialog();
                return;
            }
            base.OnKeyDown(e);
        }

        public string LastSelectedEqDir { get { return _currentEqDir; } }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            try
            {
                Rectangle wa = Screen.FromControl(this).WorkingArea;
                int maxW = Math.Max(MinimumSize.Width, wa.Width - UiScale.Px(16));
                int maxH = Math.Max(MinimumSize.Height, wa.Height - UiScale.Px(16));
                int nextW = Math.Min(Width, maxW);
                int nextH = Math.Min(Height, maxH);
                if (nextW != Width || nextH != Height)
                {
                    Size = new Size(nextW, nextH);
                }
                int nextLeft = Math.Max(wa.Left, Math.Min(Left, wa.Right - Width));
                int nextTop = Math.Max(wa.Top, Math.Min(Top, wa.Bottom - Height));
                Location = new Point(nextLeft, nextTop);
            }
            catch { }
        }

        private static float[][] CloneCoeffs(float[][] src)
        {
            if (src == null || src.Length != 11) return null;
            float[][] copy = new float[11][];
            for (int i = 0; i < 11; i++)
            {
                if (src[i] == null || src[i].Length < 5) return null;
                copy[i] = new float[] { src[i][0], src[i][1], src[i][2], src[i][3], src[i][4] };
            }
            return copy;
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            _liveSendTimer.Stop();
            if (!_confirmedExit && _livePreviewDirtiedHardware)
            {
                if (_hasRawHwRestore && _restoreRawCallback != null)
                {
                    try { _restoreRawCallback(_initialHwPreamp, CloneCoeffs(_initialHwCoeffs), _initialSnapshot.Clone(), _initialEqEnabled); } catch { }
                }
                else if (_applyCallback != null)
                {
                    try { _applyCallback(_initialSnapshot.Clone(), _initialEqEnabled, false); } catch { }
                }
            }
            base.OnFormClosing(e);
        }

        private static void AddHeaderLabel(TableLayoutPanel grid, string text, int col)
        {
            Label lbl = new Label();
            lbl.Text = text;
            lbl.AutoSize = true;
            lbl.Anchor = AnchorStyles.Left;
            grid.Controls.Add(lbl, col, 0);
        }

        private void PopulateControlsFromModel(EqProfile p)
        {
            _suppressEvents = true;
            try
            {
                _txtPreamp.Text = string.Format(CultureInfo.InvariantCulture, "{0:0.0}", p.PreampDb);
                _txtPreamp.BackColor = SystemColors.Window;
                for (int i = 0; i < 11; i++)
                {
                    EqBandSpec b = p.Bands[i];
                    _rowEnable[i].Checked = b.Enabled;
                    _rowKind[i].SelectedIndex = (int)b.Kind;
                    _rowFreq[i].Text = EqDspMath.FormatFrequencyClean(b.FrequencyHz);
                    _rowFreq[i].BackColor = SystemColors.Window;
                    _rowGainSlider[i].SetValueSilent(b.GainDb);
                    _rowGainText[i].Text = string.Format(CultureInfo.InvariantCulture, "{0:0.0}", b.GainDb);
                    _rowGainText[i].BackColor = SystemColors.Window;
                    _rowQ[i].Text = string.Format(CultureInfo.InvariantCulture, "{0:0.00}", b.Q);
                    _rowQ[i].BackColor = SystemColors.Window;
                    ApplyRowEnabledVisual(i, b.Enabled);
                }
            }
            finally
            {
                _suppressEvents = false;
            }
            RefreshGraphAndHeadroomUi();
        }

        private void ApplyRowEnabledVisual(int i, bool on)
        {
            _rowKind[i].Enabled = on;
            _rowFreq[i].Enabled = on;
            _rowGainSlider[i].Enabled = on;
            _rowGainText[i].Enabled = on;
            _rowQ[i].Enabled = on;
        }

        private void OnRowEnableChanged(int idx)
        {
            if (_suppressEvents) return;
            bool on = _rowEnable[idx].Checked;
            _working.Bands[idx].Enabled = on;
            ApplyRowEnabledVisual(idx, on);
            RefreshGraphAndHeadroomUi();
            QueueLivePreview();
        }

        private void OnRowKindChanged(int idx)
        {
            if (_suppressEvents) return;
            FilterKind prevKind = _working.Bands[idx].Kind;
            FilterKind nextKind = (FilterKind)(_rowKind[idx].SelectedIndex >= 0 ? _rowKind[idx].SelectedIndex : 0);
            _working.Bands[idx].Kind = nextKind;

            if (prevKind == FilterKind.PK && nextKind != FilterKind.PK && Math.Abs(_working.Bands[idx].Q - 1.41) < 0.03)
            {
                _working.Bands[idx].Q = 0.71;
                _rowQ[idx].Text = "0.71";
            }
            else if (prevKind != FilterKind.PK && nextKind == FilterKind.PK && Math.Abs(_working.Bands[idx].Q - 0.71) < 0.03)
            {
                _working.Bands[idx].Q = 1.41;
                _rowQ[idx].Text = "1.41";
            }

            RefreshGraphAndHeadroomUi();
            QueueLivePreview();
        }

        private void OnRowSliderChanged(int idx)
        {
            if (_suppressEvents) return;
            double gdb = _rowGainSlider[idx].ValueDb;
            _working.Bands[idx].GainDb = gdb;
            _suppressEvents = true;
            _rowGainText[idx].Text = string.Format(CultureInfo.InvariantCulture, "{0:0.0}", gdb);
            _rowGainText[idx].BackColor = SystemColors.Window;
            _suppressEvents = false;
            MarkEditedLabel();
            RefreshGraphAndHeadroomUi();
            QueueLivePreview();
        }

        private static bool TryParseFlexibleDouble(string raw, out double value)
        {
            value = 0.0;
            if (string.IsNullOrEmpty(raw)) return false;
            string normalized = raw.Trim()
                .Replace("Hz", "").Replace("hz", "")
                .Replace("dB", "").Replace("db", "")
                .Trim();
            return EqDspMath.TryParseStrictNumber(normalized, out value);
        }

        private void CommitPreampText()
        {
            if (_suppressEvents) return;
            double v;
            if (!TryParseFlexibleDouble(_txtPreamp.Text, out v))
            {
                _txtPreamp.BackColor = Color.FromArgb(255, 228, 228);
                return;
            }
            v = EqDspMath.Clamp(Math.Round(v * 10.0) / 10.0, -36.0, 12.0);
            _working.PreampDb = v;
            _txtPreamp.Text = string.Format(CultureInfo.InvariantCulture, "{0:0.0}", v);
            RefreshGraphAndHeadroomUi();
            QueueLivePreview();
        }

        private void CommitRowFreq(int idx)
        {
            if (_suppressEvents) return;
            double v;
            if (!TryParseFlexibleDouble(_rowFreq[idx].Text, out v))
            {
                _rowFreq[idx].BackColor = Color.FromArgb(255, 228, 228);
                return;
            }
            v = EqDspMath.Clamp(Math.Round(v * 10.0) / 10.0, 10.0, 23500.0);
            _working.Bands[idx].FrequencyHz = v;
            _rowFreq[idx].Text = EqDspMath.FormatFrequencyClean(v);
            _rowFreq[idx].BackColor = SystemColors.Window;
            MarkEditedLabel();
            RefreshGraphAndHeadroomUi();
            QueueLivePreview();
        }

        private void CommitRowGainText(int idx)
        {
            if (_suppressEvents) return;
            double v;
            if (!TryParseFlexibleDouble(_rowGainText[idx].Text, out v))
            {
                _rowGainText[idx].BackColor = Color.FromArgb(255, 228, 228);
                return;
            }
            v = EqDspMath.Clamp(Math.Round(v * 10.0) / 10.0, -30.0, 24.0);
            _working.Bands[idx].GainDb = v;
            _suppressEvents = true;
            _rowGainText[idx].Text = string.Format(CultureInfo.InvariantCulture, "{0:0.0}", v);
            _rowGainText[idx].BackColor = SystemColors.Window;
            _rowGainSlider[idx].SetValueSilent(v);
            _suppressEvents = false;
            MarkEditedLabel();
            RefreshGraphAndHeadroomUi();
            QueueLivePreview();
        }

        private void CommitRowQ(int idx)
        {
            if (_suppressEvents) return;
            double v;
            if (!TryParseFlexibleDouble(_rowQ[idx].Text, out v))
            {
                _rowQ[idx].BackColor = Color.FromArgb(255, 228, 228);
                return;
            }
            v = EqDspMath.Clamp(Math.Round(v * 100.0) / 100.0, 0.06, 30.0);
            _working.Bands[idx].Q = v;
            _rowQ[idx].Text = string.Format(CultureInfo.InvariantCulture, "{0:0.00}", v);
            _rowQ[idx].BackColor = SystemColors.Window;
            MarkEditedLabel();
            RefreshGraphAndHeadroomUi();
            QueueLivePreview();
        }

        private void MarkEditedLabel()
        {
            if (_working.SourceLabel != "手動編集")
            {
                _working.SourceLabel = "手動編集";
            }
        }

        private void RefreshGraphAndHeadroomUi()
        {
            _graph.SetProfile(_working, _chkEqEnable.Checked);
            bool clippingRisk = _chkEqEnable.Checked && _graph.PeakWithPreampDb > 0.05;
            _txtPreamp.BackColor = clippingRisk ? Color.FromArgb(255, 232, 232) : SystemColors.Window;

            double idealPre = _graph.RawFilterPeakDb > 0.01
                ? (-Math.Ceiling(_graph.RawFilterPeakDb * 10.0) / 10.0)
                : 0.0;
            _btnAutoPreamp.Text = idealPre < -0.01
                ? string.Format(CultureInfo.InvariantCulture, "自動調整 ({0:0.0})", idealPre)
                : "自動調整 (0.0)";
        }

        private void OnAutoPreampClick(object sender, EventArgs e)
        {
            CommitAllTextBoxes();
            double peakWithPre;
            double rawPeak = EqDspMath.ComputePeakFilterBoostDb(_working, out peakWithPre);
            double idealPre = rawPeak > 0.01
                ? (-Math.Ceiling(rawPeak * 10.0) / 10.0)
                : 0.0;
            idealPre = EqDspMath.Clamp(idealPre, -36.0, 0.0);
            _working.PreampDb = idealPre;
            _suppressEvents = true;
            _txtPreamp.Text = string.Format(CultureInfo.InvariantCulture, "{0:0.0}", idealPre);
            _suppressEvents = false;
            RefreshGraphAndHeadroomUi();
            QueueLivePreview();
        }

        private void OnApplyTemplateClick(object sender, EventArgs e)
        {
            EqProfile next;
            int sel = _cmbTemplate.SelectedIndex;
            if (sel == 0) next = new EqProfile();
            else if (sel == 1) next = EqProfile.CreateGraphic10BandTemplate();
            else if (sel == 2) next = EqProfile.CreateBassBoostSample();
            else if (sel == 3) next = EqProfile.CreateVoiceFootstepSample();
            else
            {
                next = _working.Clone();
                next.PreampDb = 0.0;
                next.SourceLabel = "フラット";
                for (int i = 0; i < 11; i++) next.Bands[i].GainDb = 0.0;
            }

            _working.PreampDb = next.PreampDb;
            _working.SourceLabel = next.SourceLabel;
            for (int i = 0; i < 11; i++) _working.Bands[i] = next.Bands[i];
            PopulateControlsFromModel(_working);
            QueueLivePreview();
        }

        private void QueueLivePreview()
        {
            if (!_chkLivePreview.Checked || _applyCallback == null) return;
            _liveSendTimer.Stop();
            _liveSendTimer.Start();
        }

        private void OnLiveSendTimerTick(object sender, EventArgs e)
        {
            _liveSendTimer.Stop();
            string err;
            if (!ValidateWorkingModel(out err)) return;
            if (_applyCallback != null)
            {
                _livePreviewDirtiedHardware = true;
                _applyCallback(_working.Clone(), _chkEqEnable.Checked, false);
            }
        }

        private void CommitAllTextBoxes()
        {
            CommitPreampText();
            for (int i = 0; i < 11; i++)
            {
                CommitRowFreq(i);
                CommitRowGainText(i);
                CommitRowQ(i);
            }
        }

        private bool ValidateWorkingModel(out string error)
        {
            EqDspMath.ComputePreampLinear(_working.PreampDb, out error);
            if (error != null) return false;
            for (int i = 0; i < 11; i++)
            {
                if (EqDspMath.ComputeBiquad(_working.Bands[i], i + 1, out error) == null)
                {
                    return false;
                }
            }
            return true;
        }

        private bool CommitAndValidateWithDialog()
        {
            CommitAllTextBoxes();
            string err;
            if (!ValidateWorkingModel(out err))
            {
                MessageBox.Show(this, err, "設定エラー", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            return true;
        }

        private void OnLoadPeaceClick(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "EQ設定ファイルを開く (.peace / .txt)";
                dlg.Filter = "Peace / Equalizer APO 設定 (*.peace;*.txt)|*.peace;*.txt|すべてのファイル (*.*)|*.*";
                if (!string.IsNullOrEmpty(_currentEqDir) && Directory.Exists(_currentEqDir))
                {
                    dlg.InitialDirectory = _currentEqDir;
                }
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    _currentEqDir = Path.GetDirectoryName(dlg.FileName);
                    EqProfile loaded = EqDspMath.ParsePeaceFile(dlg.FileName);
                    ApplyLoadedProfileToEditor(loaded);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "読み込みエラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void ApplyLoadedProfileToEditor(EqProfile loaded)
        {
            _working.PreampDb = loaded.PreampDb;
            _working.SourceLabel = loaded.SourceLabel;
            for (int i = 0; i < 11; i++) _working.Bands[i] = loaded.Bands[i];
            PopulateControlsFromModel(_working);
            QueueLivePreview();
        }

        private void OnOpenAiPasteClick(object sender, EventArgs e)
        {
            using (AiEqPasteForm dlg = new AiEqPasteForm(_appIcon))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.ParsedProfile != null)
                {
                    ApplyLoadedProfileToEditor(dlg.ParsedProfile);
                }
            }
        }

        private void TryPasteOrOpenAiDialog()
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    EqProfile parsed = EqDspMath.ParsePeaceText(Clipboard.GetText(), "AI・テキスト貼付");
                    ApplyLoadedProfileToEditor(parsed);
                    return;
                }
            }
            catch { }
            OnOpenAiPasteClick(this, EventArgs.Empty);
        }

        private void OnEqDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                e.Effect = DragDropEffects.Copy;
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        private void OnEqDragDrop(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null && files.Length > 0)
                    {
                        _currentEqDir = Path.GetDirectoryName(files[0]);
                        ApplyLoadedProfileToEditor(EqDspMath.ParsePeaceFile(files[0]));
                        return;
                    }
                }
                if (e.Data.GetDataPresent(DataFormats.UnicodeText))
                {
                    string txt = e.Data.GetData(DataFormats.UnicodeText) as string;
                    if (!string.IsNullOrEmpty(txt))
                    {
                        ApplyLoadedProfileToEditor(EqDspMath.ParsePeaceText(txt, "AI・テキスト貼付"));
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "読み込みエラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void OnExportPeaceClick(object sender, EventArgs e)
        {
            if (!CommitAndValidateWithDialog()) return;

            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "EQ設定をファイルに保存";
                dlg.Filter = "Equalizer APO テキスト (*.txt)|*.txt";
                dlg.FileName = "my-biwamasu-eq.txt";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    EqDspMath.SavePeaceFile(dlg.FileName, _working);
                    _working.SourceLabel = Path.GetFileName(dlg.FileName);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "保存に失敗しました: " + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void OnApplyClick(object sender, EventArgs e)
        {
            if (!CommitAndValidateWithDialog()) return;
            _liveSendTimer.Stop();
            if (_applyCallback != null)
            {
                _applyCallback(_working.Clone(), _chkEqEnable.Checked, true);
                CopyProfile(_working, _initialSnapshot);
                _initialEqEnabled = _chkEqEnable.Checked;
                _hasRawHwRestore = false;
                _livePreviewDirtiedHardware = false;
            }
        }

        private void OnOkClick(object sender, EventArgs e)
        {
            if (!CommitAndValidateWithDialog()) return;
            _liveSendTimer.Stop();
            _confirmedExit = true;
            if (_applyCallback != null)
            {
                _applyCallback(_working.Clone(), _chkEqEnable.Checked, true);
            }
            DialogResult = DialogResult.OK;
            Close();
        }

        private static void CopyProfile(EqProfile src, EqProfile dst)
        {
            dst.PreampDb = src.PreampDb;
            dst.SourceLabel = src.SourceLabel;
            for (int i = 0; i < 11; i++) dst.Bands[i] = src.Bands[i].Clone();
        }
    }

    internal sealed class MainForm : Form
    {
        private readonly HidDeviceConnection _hid;
        private readonly string _appBaseDir;
        private readonly string _localEqCachePath;
        private readonly FavoritePresetStore _favoriteStore;
        private readonly Icon _appIcon;

        private readonly Label _lblConnState;
        private readonly Label _lblSignalDetail;
        private readonly GainSlider _sldMaster;
        private readonly NumericUpDown _numMaster;
        private readonly GainSlider _sldGame;
        private readonly NumericUpDown _numGame;
        private readonly GainSlider _sldPc;
        private readonly NumericUpDown _numPc;
        private readonly CheckBox _chkEq;
        private readonly Label _lblEqSummary;
        private readonly Button _btnTestTone;
        private readonly Button _btnSaveFlash;
        private readonly ToolStripStatusLabel _statusText;
        private readonly System.Windows.Forms.Timer _pollTimer;
        private readonly System.Windows.Forms.Timer _sendDebounceTimer;

        private EqProfile _eqProfile;
        private string _lastEqDir;
        private bool _hasLocalEqCache;
        private bool _deviceFlashLoaded;
        private uint _lastPollClipCount;
        private bool _recentClipDetected;
        private bool _eqBypassLoudnessHint;
        private bool _syncingUi;
        private bool _userDraggingSlider;
        private bool _pendingParamSend;
        private bool _deviceConnected;
        private bool _testToneActive;
        private bool _hasUnsavedChanges;
        private byte _lastRxPinMode = 13;
        private float _lastHwPreampLin = 1.0f;
        private float[][] _lastHwCoeffs;

        public MainForm(ushort vid, ushort pid)
        {
            _hid = new HidDeviceConnection(vid, pid);
            _appBaseDir = AppDomain.CurrentDomain.BaseDirectory;
            _localEqCachePath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "BiwamasuMixer",
                "active-eq.txt");
            _favoriteStore = new FavoritePresetStore(FavoritePresetStore.GetDefaultDirectory());
            _lastEqDir = DetectInitialEqDirectory();
            _eqProfile = LoadCachedOrSampleEq();
            _appIcon = AppIconFactory.CreateAppIcon();

            AutoScaleMode = AutoScaleMode.None;
            Font = UiScale.CreateUiFont();
            if (_appIcon != null) Icon = _appIcon;
            Text = "Biwamasu-Mixer Setting tool";
            KeyPreview = true;
            AllowDrop = true;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(UiScale.Px(596), UiScale.Px(440));

            MenuStrip menu = BuildMenuStrip();
            menu.Font = Font;
            MainMenuStrip = menu;

            StatusStrip statusStrip = new StatusStrip();
            statusStrip.Font = Font;
            statusStrip.SizingGrip = false;
            _statusText = new ToolStripStatusLabel("準備完了");
            statusStrip.Items.Add(_statusText);

            TableLayoutPanel mainLayout = new TableLayoutPanel();
            mainLayout.Dock = DockStyle.Fill;
            mainLayout.Padding = UiScale.Pad(12, 8, 12, 8);
            mainLayout.ColumnCount = 1;
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            mainLayout.RowCount = 4;
            mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));

            // 1. Connection
            GroupBox grpConn = new GroupBox();
            grpConn.Text = "接続状態";
            grpConn.Dock = DockStyle.Fill;
            grpConn.AutoSize = true;
            grpConn.Margin = UiScale.Pad(0, 0, 0, 6);
            grpConn.Padding = UiScale.Pad(10, 6, 10, 6);

            TableLayoutPanel connGrid = new TableLayoutPanel();
            connGrid.Dock = DockStyle.Fill;
            connGrid.AutoSize = true;
            connGrid.ColumnCount = 2;
            connGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            connGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _lblConnState = new Label();
            _lblConnState.Text = "状態: 確認中...";
            _lblConnState.Dock = DockStyle.Fill;
            _lblConnState.AutoSize = true;
            _lblConnState.Margin = UiScale.Pad(0, 0, 8, 3);

            _lblSignalDetail = new Label();
            _lblSignalDetail.Text = "Switch 2: ---  |  OBS: ---";
            _lblSignalDetail.ForeColor = SystemColors.ControlDarkDark;
            _lblSignalDetail.Dock = DockStyle.Fill;
            _lblSignalDetail.AutoSize = true;
            _lblSignalDetail.Margin = UiScale.Pad(0, 0, 8, 0);

            Button btnRefresh = UiScale.CreateButton("再読込(&R)", 86, 26);
            btnRefresh.Margin = new Padding(0);
            btnRefresh.Anchor = AnchorStyles.Right | AnchorStyles.Top;
            btnRefresh.Click += delegate { RefreshFromDevice(true); };

            connGrid.Controls.Add(_lblConnState, 0, 0);
            connGrid.Controls.Add(btnRefresh, 1, 0);
            connGrid.SetRowSpan(btnRefresh, 2);
            connGrid.Controls.Add(_lblSignalDetail, 0, 1);
            grpConn.Controls.Add(connGrid);

            // 2. Volume (clean, zero explanation box — red region & numeric tint convey non-recommended amplification)
            GroupBox grpVol = new GroupBox();
            grpVol.Text = "音量バランス";
            grpVol.Dock = DockStyle.Fill;
            grpVol.AutoSize = true;
            grpVol.Margin = UiScale.Pad(0, 0, 0, 6);
            grpVol.Padding = UiScale.Pad(10, 8, 10, 8);

            TableLayoutPanel volGrid = new TableLayoutPanel();
            volGrid.Dock = DockStyle.Fill;
            volGrid.AutoSize = true;
            volGrid.ColumnCount = 4;
            volGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiScale.Pxf(118f)));
            volGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            volGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiScale.Pxf(74f)));
            volGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiScale.Pxf(28f)));

            AddVolumeRow(
                volGrid, 0, "マスター音量:",
                -60.0m, 6.0m, -45.0m, -45.0, false,
                new double[] { -60, -45, -30, -15, 0, 6 },
                out _sldMaster, out _numMaster);

            AddVolumeRow(
                volGrid, 1, "Switch 2 補正:",
                -60.0m, 12.0m, 0.0m, 0.0, true,
                new double[] { -60, -45, -30, -15, 0, 12 },
                out _sldGame, out _numGame);

            AddVolumeRow(
                volGrid, 2, "PC 音量補正:",
                -24.0m, 12.0m, 0.0m, 0.0, true,
                new double[] { -24, -12, 0, 12 },
                out _sldPc, out _numPc);

            grpVol.Controls.Add(volGrid);

            // 3. Equalizer
            GroupBox grpEq = new GroupBox();
            grpEq.Text = "イコライザー";
            grpEq.Dock = DockStyle.Fill;
            grpEq.AutoSize = true;
            grpEq.Margin = UiScale.Pad(0, 0, 0, 6);
            grpEq.Padding = UiScale.Pad(10, 6, 10, 7);

            TableLayoutPanel eqGrid = new TableLayoutPanel();
            eqGrid.Dock = DockStyle.Fill;
            eqGrid.AutoSize = true;
            eqGrid.ColumnCount = 2;
            eqGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            eqGrid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            _chkEq = new CheckBox();
            _chkEq.Text = "有効にする(&E)";
            _chkEq.AutoSize = true;
            _chkEq.Anchor = AnchorStyles.Left;
            _chkEq.Margin = UiScale.Pad(2, 3, 8, 4);
            _chkEq.CheckedChanged += OnEqCheckedChanged;

            FlowLayoutPanel eqBtns = new FlowLayoutPanel();
            eqBtns.AutoSize = true;
            eqBtns.WrapContents = false;
            eqBtns.Anchor = AnchorStyles.Right;
            eqBtns.Margin = new Padding(0);

            Button btnLoadPeace = UiScale.CreateButton("ファイル読込(&L)...", 114, 26);
            btnLoadPeace.Click += OnLoadPeaceMainClick;
            Button btnPasteAi = UiScale.CreateButton("AI・テキスト貼付(&P)...", 138, 26);
            btnPasteAi.Click += OnOpenAiPasteMainClick;
            Button btnDetailEq = UiScale.CreateButton("詳細設定(&D)...", 98, 26);
            btnDetailEq.Margin = UiScale.Pad(0, 2, 0, 2);
            btnDetailEq.Click += OnOpenEqEditorClick;

            eqBtns.Controls.Add(btnLoadPeace);
            eqBtns.Controls.Add(btnPasteAi);
            eqBtns.Controls.Add(btnDetailEq);

            _lblEqSummary = new Label();
            _lblEqSummary.ForeColor = SystemColors.ControlDarkDark;
            _lblEqSummary.Dock = DockStyle.Fill;
            _lblEqSummary.AutoSize = true;
            _lblEqSummary.Margin = UiScale.Pad(2, 3, 2, 0);

            eqGrid.Controls.Add(_chkEq, 0, 0);
            eqGrid.Controls.Add(eqBtns, 1, 0);
            eqGrid.Controls.Add(_lblEqSummary, 0, 1);
            eqGrid.SetColumnSpan(_lblEqSummary, 2);
            grpEq.Controls.Add(eqGrid);

            // 4. Bottom buttons
            TableLayoutPanel bottomBar = new TableLayoutPanel();
            bottomBar.Dock = DockStyle.Bottom;
            bottomBar.AutoSize = true;
            bottomBar.ColumnCount = 2;
            bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            bottomBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
            bottomBar.Margin = UiScale.Pad(0, 4, 0, 0);

            FlowLayoutPanel leftBtns = new FlowLayoutPanel();
            leftBtns.AutoSize = true;
            leftBtns.WrapContents = false;
            leftBtns.Anchor = AnchorStyles.Left;
            leftBtns.Margin = new Padding(0);

            _btnTestTone = UiScale.CreateButton("テスト音: OFF", 116, 28);
            _btnTestTone.Click += OnToggleTestToneClick;

            Button btnReset = UiScale.CreateButton("初期値に戻す", 104, 28);
            btnReset.Margin = UiScale.Pad(0, 2, 0, 2);
            btnReset.Click += OnResetDefaultsClick;

            leftBtns.Controls.Add(_btnTestTone);
            leftBtns.Controls.Add(btnReset);

            FlowLayoutPanel rightBtns = new FlowLayoutPanel();
            rightBtns.FlowDirection = FlowDirection.RightToLeft;
            rightBtns.Dock = DockStyle.Fill;
            rightBtns.AutoSize = true;
            rightBtns.WrapContents = false;
            rightBtns.Anchor = AnchorStyles.Right;
            rightBtns.Margin = new Padding(0);

            Button btnClose = UiScale.CreateButton("閉じる", 80, 28);
            btnClose.Margin = UiScale.Pad(6, 2, 0, 2);
            btnClose.Click += delegate { Close(); };

            _btnSaveFlash = UiScale.CreateButton("本体に保存(&S)", 128, 28);
            _btnSaveFlash.Margin = new Padding(0, UiScale.Px(2), 0, UiScale.Px(2));
            _btnSaveFlash.Click += OnSaveFlashClick;

            rightBtns.Controls.Add(btnClose);
            rightBtns.Controls.Add(_btnSaveFlash);

            bottomBar.Controls.Add(leftBtns, 0, 0);
            bottomBar.Controls.Add(rightBtns, 1, 0);

            mainLayout.Controls.Add(grpConn, 0, 0);
            mainLayout.Controls.Add(grpVol, 0, 1);
            mainLayout.Controls.Add(grpEq, 0, 2);
            mainLayout.Controls.Add(bottomBar, 0, 3);

            Controls.Add(mainLayout);
            Controls.Add(statusStrip);
            Controls.Add(menu);

            DragEnter += OnMainDragEnter;
            DragDrop += OnMainDragDrop;

            UpdateGainWarningsAndThresholds();
            UpdateEqSummaryLabel();
            SetUnsavedState(false);

            _sendDebounceTimer = new System.Windows.Forms.Timer();
            _sendDebounceTimer.Interval = 35;
            _sendDebounceTimer.Tick += OnSendDebounceTick;

            _pollTimer = new System.Windows.Forms.Timer();
            _pollTimer.Interval = 2000;
            _pollTimer.Tick += delegate
            {
                if (!_userDraggingSlider && !_pendingParamSend)
                {
                    RefreshFromDevice(false);
                }
            };
        }

        public void SetPreviewLevelsForTest(double masterDb, double gameDb, double pcDb)
        {
            _syncingUi = true;
            try
            {
                SetPairValue(_sldMaster, _numMaster, masterDb);
                SetPairValue(_sldGame, _numGame, gameDb);
                SetPairValue(_sldPc, _numPc, pcDb);
                UpdateGainWarningsAndThresholds();
            }
            finally
            {
                _syncingUi = false;
            }
        }

        public bool VerifyEqBypassHintForTest()
        {
            EqProfile saved = _eqProfile;
            try
            {
                SetPreviewLevelsForTest(-45.0, 0.0, 0.0);
                EqProfile deep = new EqProfile();
                deep.PreampDb = -10.5;
                _eqProfile = deep;

                _syncingUi = true;
                _chkEq.Checked = true;
                _syncingUi = false;

                _chkEq.Checked = false;
                if (!_statusText.Text.Contains("-10.5 dB")) return false;

                _chkEq.Checked = true;
                if (_statusText.Text.Contains("-10.5 dB")) return false;
                return true;
            }
            finally
            {
                _eqProfile = saved;
                _eqBypassLoudnessHint = false;
                _syncingUi = true;
                _chkEq.Checked = false;
                _syncingUi = false;
                SetUnsavedState(false);
            }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            RefreshFromDevice(false);
            _pollTimer.Start();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (_deviceConnected && _hasUnsavedChanges && e.CloseReason == CloseReason.UserClosing)
            {
                DialogResult ans = MessageBox.Show(
                    this,
                    "変更した設定がまだ本体メモリに保存されていません。\r\n保存してから閉じますか？",
                    "Biwamasu Mixer",
                    MessageBoxButtons.YesNoCancel,
                    MessageBoxIcon.Question);
                if (ans == DialogResult.Cancel)
                {
                    e.Cancel = true;
                    return;
                }
                if (ans == DialogResult.Yes)
                {
                    try
                    {
                        ExecuteVerifiedFlashSave();
                    }
                    catch (Exception ex)
                    {
                        MessageBox.Show(this, "保存に失敗しました: " + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                        e.Cancel = true;
                        return;
                    }
                }
            }

            _pollTimer.Stop();
            _sendDebounceTimer.Stop();
            if (_deviceConnected && _testToneActive)
            {
                try { _hid.SetTestTone(false); } catch { }
            }
            _hid.Dispose();
            if (_appIcon != null) _appIcon.Dispose();
            base.OnFormClosing(e);
        }

        private MenuStrip BuildMenuStrip()
        {
            MenuStrip ms = new MenuStrip();

            ToolStripMenuItem mFile = new ToolStripMenuItem("ファイル(&F)");
            ToolStripMenuItem miOpenPeace = new ToolStripMenuItem("EQファイルを読み込む(&O)...");
            miOpenPeace.Click += OnLoadPeaceMainClick;
            ToolStripMenuItem miPasteAi = new ToolStripMenuItem("AI作成・テキスト貼付から読込(&P)...");
            miPasteAi.ShortcutKeys = Keys.Control | Keys.Shift | Keys.V;
            miPasteAi.Click += OnOpenAiPasteMainClick;
            ToolStripMenuItem miSavePeace = new ToolStripMenuItem("EQ設定をファイルに保存(&S)...");
            miSavePeace.Click += OnExportPeaceMainClick;
            ToolStripMenuItem miExit = new ToolStripMenuItem("終了(&X)");
            miExit.Click += delegate { Close(); };
            mFile.DropDownItems.Add(miOpenPeace);
            mFile.DropDownItems.Add(miPasteAi);
            mFile.DropDownItems.Add(miSavePeace);
            mFile.DropDownItems.Add(new ToolStripSeparator());
            mFile.DropDownItems.Add(miExit);

            ToolStripMenuItem mFavorites = new ToolStripMenuItem("お気に入り(&P)");
            ToolStripMenuItem miFavAdd = new ToolStripMenuItem("現在の設定をお気に入りに追加(&A)...");
            miFavAdd.ShortcutKeys = Keys.Control | Keys.D;
            miFavAdd.Click += OnAddFavoriteClick;
            ToolStripMenuItem miFavManage = new ToolStripMenuItem("お気に入りの管理・削除(&M)...");
            miFavManage.Click += OnManageFavoritesClick;
            mFavorites.DropDownItems.Add(miFavAdd);
            mFavorites.DropDownItems.Add(miFavManage);
            mFavorites.DropDownItems.Add(new ToolStripSeparator());
            int favFixedCount = mFavorites.DropDownItems.Count;
            mFavorites.DropDownOpening += delegate
            {
                RebuildFavoritesMenu(mFavorites, favFixedCount);
            };

            ToolStripMenuItem mTools = new ToolStripMenuItem("ツール(&T)");
            ToolStripMenuItem miEqEditor = new ToolStripMenuItem("イコライザー詳細設定(&E)...");
            miEqEditor.Click += OnOpenEqEditorClick;
            ToolStripMenuItem miRefresh = new ToolStripMenuItem("状態を再読込(&R)");
            miRefresh.Click += delegate { RefreshFromDevice(true); };
            ToolStripMenuItem miWinSound = new ToolStripMenuItem("Windows サウンド設定を開く(&W)...");
            miWinSound.Click += delegate
            {
                try { Process.Start("mmsys.cpl"); }
                catch (Exception ex) { MessageBox.Show(this, ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            };
            ToolStripMenuItem miReset = new ToolStripMenuItem("初期値に戻す(&D)...");
            miReset.Click += OnResetDefaultsClick;
            ToolStripMenuItem miBootsel = new ToolStripMenuItem("BOOTSELモードへ切替(&B)...");
            miBootsel.Click += OnEnterBootselClick;
            mTools.DropDownItems.Add(miEqEditor);
            mTools.DropDownItems.Add(miRefresh);
            mTools.DropDownItems.Add(miWinSound);
            mTools.DropDownItems.Add(new ToolStripSeparator());
            mTools.DropDownItems.Add(miReset);
            mTools.DropDownItems.Add(miBootsel);

            ToolStripMenuItem mHelp = new ToolStripMenuItem("ヘルプ(&H)");
            ToolStripMenuItem miGuide = new ToolStripMenuItem("使い方・各項目ガイド(&G)...");
            miGuide.ShortcutKeys = Keys.F1;
            miGuide.Click += delegate
            {
                using (HelpGuideForm hf = new HelpGuideForm(_appIcon, 0))
                {
                    hf.ShowDialog(this);
                }
            };
            ToolStripMenuItem miAbout = new ToolStripMenuItem("バージョン情報(&A)...");
            miAbout.Click += delegate
            {
                MessageBox.Show(
                    this,
                    string.Format(
                        "Biwamasu-Mixer Setting tool v1.4\r\n" +
                        "対象USB ID: VID_{0} / PID_{1}\r\n\r\n" +
                        "Copyright (c) 2026 mugicha_xtx\r\n" +
                        "MIT License",
                        _hid.VidUpper, _hid.PidUpper),
                    "バージョン情報",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            };
            mHelp.DropDownItems.Add(miGuide);
            mHelp.DropDownItems.Add(new ToolStripSeparator());
            mHelp.DropDownItems.Add(miAbout);

            ms.Items.Add(mFile);
            ms.Items.Add(mFavorites);
            ms.Items.Add(mTools);
            ms.Items.Add(mHelp);
            return ms;
        }

        private void RebuildFavoritesMenu(ToolStripMenuItem menu, int fixedCount)
        {
            while (menu.DropDownItems.Count > fixedCount)
            {
                ToolStripItem old = menu.DropDownItems[menu.DropDownItems.Count - 1];
                menu.DropDownItems.RemoveAt(menu.DropDownItems.Count - 1);
                old.Dispose();
            }

            List<FavoritePreset> list = _favoriteStore.ListAll();
            if (list.Count == 0)
            {
                ToolStripMenuItem empty = new ToolStripMenuItem("(登録されたお気に入りはありません)");
                empty.Enabled = false;
                menu.DropDownItems.Add(empty);
                return;
            }

            for (int i = 0; i < list.Count; i++)
            {
                FavoritePreset fp = list[i];
                ToolStripMenuItem item = new ToolStripMenuItem(fp.FormatMenuText());
                item.Click += delegate { ApplyFavoritePreset(fp); };
                menu.DropDownItems.Add(item);
            }
        }

        private void AddVolumeRow(
            TableLayoutPanel grid,
            int row,
            string title,
            decimal minDb,
            decimal maxDb,
            decimal initDb,
            double resetDb,
            bool bipolar,
            double[] ticks,
            out GainSlider sld,
            out NumericUpDown num)
        {
            Label lbl = new Label();
            lbl.Text = title;
            lbl.AutoSize = true;
            lbl.Anchor = AnchorStyles.Left;
            grid.Controls.Add(lbl, 0, row);

            GainSlider gs = new GainSlider();
            gs.MinimumDb = (double)minDb;
            gs.MaximumDb = (double)maxDb;
            gs.DefaultResetDb = resetDb;
            gs.BipolarFromZero = bipolar;
            gs.ShowScaleLabels = true;
            gs.ScaleTicks = ticks;
            gs.SetValueSilent((double)initDb);
            gs.Height = UiScale.Px(34);
            gs.Dock = DockStyle.Fill;
            gs.Margin = UiScale.Pad(0, 2, 8, 2);

            NumericUpDown ud = new NumericUpDown();
            ud.DecimalPlaces = 1;
            ud.Minimum = minDb;
            ud.Maximum = maxDb;
            ud.Increment = 0.5m;
            ud.Value = initDb;
            ud.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            ud.Margin = UiScale.Pad(0, 4, 2, 4);

            Label unit = new Label();
            unit.Text = "dB";
            unit.AutoSize = true;
            unit.Anchor = AnchorStyles.Left;

            grid.Controls.Add(gs, 1, row);
            grid.Controls.Add(ud, 2, row);
            grid.Controls.Add(unit, 3, row);

            gs.DragStarted += delegate { _userDraggingSlider = true; };
            gs.DragCompleted += delegate
            {
                _userDraggingSlider = false;
                FlushPendingParamWrite();
            };
            gs.ValueChanged += delegate
            {
                if (_syncingUi) return;
                _recentClipDetected = false;
                _eqBypassLoudnessHint = false;
                _syncingUi = true;
                ud.Value = ClampDec(gs.ValueDb, ud.Minimum, ud.Maximum);
                UpdateGainWarningsAndThresholds();
                _syncingUi = false;
                SetUnsavedState(true);
                QueueParamWrite();
            };
            ud.ValueChanged += delegate
            {
                if (_syncingUi) return;
                _recentClipDetected = false;
                _eqBypassLoudnessHint = false;
                _syncingUi = true;
                gs.SetValueSilent((double)ud.Value);
                UpdateGainWarningsAndThresholds();
                _syncingUi = false;
                SetUnsavedState(true);
                QueueParamWrite();
            };

            sld = gs;
            num = ud;
        }

        private void UpdateGainWarningsAndThresholds()
        {
            double master = (double)_numMaster.Value;
            double game = (double)_numGame.Value;
            double pc = (double)_numPc.Value;

            double trimThreshold = -master;
            double maxPositiveTrim = Math.Max(0.0, Math.Max(game, pc));
            double masterThreshold = -maxPositiveTrim;

            _sldGame.DangerThresholdDb = trimThreshold;
            _sldPc.DangerThresholdDb = trimThreshold;
            _sldMaster.DangerThresholdDb = masterThreshold;

            double swNet = master + game;
            double pcNet = master + pc;
            bool swOver = swNet > 0.04;
            bool pcOver = pcNet > 0.04;
            bool masterOver = master > masterThreshold + 0.04;

            Color warnFieldBg = Color.FromArgb(255, 230, 230);
            Color warnFieldFg = Color.FromArgb(164, 16, 16);

            _numMaster.BackColor = masterOver ? warnFieldBg : SystemColors.Window;
            _numMaster.ForeColor = masterOver ? warnFieldFg : SystemColors.WindowText;

            _numGame.BackColor = swOver ? warnFieldBg : SystemColors.Window;
            _numGame.ForeColor = swOver ? warnFieldFg : SystemColors.WindowText;

            _numPc.BackColor = pcOver ? warnFieldBg : SystemColors.Window;
            _numPc.ForeColor = pcOver ? warnFieldFg : SystemColors.WindowText;

            if (swOver || pcOver)
            {
                _statusText.ForeColor = Color.FromArgb(164, 20, 20);
                _statusText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "非推奨: 合計ゲインが 0 dB を超えています (Switch 2: {0:+0.0;-0.0;0.0} dB / PC: {1:+0.0;-0.0;0.0} dB)",
                    swNet, pcNet);
            }
            else if (_recentClipDetected)
            {
                _statusText.ForeColor = Color.FromArgb(164, 20, 20);
                _statusText.Text = "音割れ(クリッピング)を検知: マスター音量またはEQプリアンプを下げてください";
            }
            else if (_eqBypassLoudnessHint && !_chkEq.Checked && _eqProfile != null && _eqProfile.PreampDb <= -8.0)
            {
                _statusText.ForeColor = Color.FromArgb(140, 82, 0);
                _statusText.Text = string.Format(
                    CultureInfo.InvariantCulture,
                    "※EQオフによりプリアンプ ({0:0.0} dB) が解除され音量が上がっています",
                    _eqProfile.PreampDb);
            }
            else if (_statusText.ForeColor != SystemColors.ControlText)
            {
                _statusText.ForeColor = SystemColors.ControlText;
                _statusText.Text = _deviceConnected ? "接続済み" : "準備完了";
            }
        }

        private void SetUnsavedState(bool dirty)
        {
            _hasUnsavedChanges = dirty;
            if (_btnSaveFlash != null)
            {
                _btnSaveFlash.Text = dirty ? "本体に保存(&S) *" : "本体に保存(&S)";
            }
        }

        private string DetectInitialEqDirectory()
        {
            try
            {
                string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
                if (!string.IsNullOrEmpty(pf))
                {
                    string apoCfg = Path.Combine(pf, "EqualizerAPO", "config");
                    if (Directory.Exists(apoCfg)) return apoCfg;
                }
            }
            catch { }
            return FindSamplePresetsDir();
        }

        private EqProfile LoadCachedOrSampleEq()
        {
            _hasLocalEqCache = false;
            try
            {
                if (File.Exists(_localEqCachePath))
                {
                    EqProfile cached = EqDspMath.ParsePeaceFile(_localEqCachePath);
                    bool isOldFlatDefault = Math.Abs(cached.PreampDb) < 0.01 &&
                                            cached.Bands[0].Kind == FilterKind.LSC &&
                                            Math.Abs(cached.Bands[0].FrequencyHz - 31.5) < 0.2;
                    if (isOldFlatDefault)
                    {
                        bool allZero = true;
                        for (int i = 0; i < 11; i++)
                        {
                            if (Math.Abs(cached.Bands[i].GainDb) > 0.01) { allZero = false; break; }
                        }
                        if (allZero) return new EqProfile();
                    }
                    _hasLocalEqCache = true;
                    cached.SourceLabel = "前回の設定";
                    return cached;
                }
            }
            catch { }

            string samplePath = FindSamplePresetsDir();
            if (!string.IsNullOrEmpty(samplePath))
            {
                string sampleFile = Path.Combine(samplePath, "sample-peace.txt");
                if (File.Exists(sampleFile))
                {
                    try { return EqDspMath.ParsePeaceFile(sampleFile); } catch { }
                }
            }
            return new EqProfile();
        }

        private void SaveCachedEq(EqProfile profile)
        {
            _hasLocalEqCache = true;
            try
            {
                EqDspMath.SavePeaceFile(_localEqCachePath, profile);
            }
            catch { }
        }

        private string FindSamplePresetsDir()
        {
            string p1 = Path.Combine(_appBaseDir, "presets");
            if (Directory.Exists(p1)) return p1;
            DirectoryInfo parent = Directory.GetParent(_appBaseDir.TrimEnd('\\', '/'));
            if (parent != null)
            {
                string p2 = Path.Combine(parent.FullName, "presets");
                if (Directory.Exists(p2)) return p2;
            }
            return _appBaseDir;
        }

        private void UpdateEqSummaryLabel()
        {
            if (!_hasLocalEqCache && _deviceConnected && _deviceFlashLoaded && _chkEq != null && _chkEq.Checked)
            {
                _lblEqSummary.ForeColor = SystemColors.ControlDarkDark;
                _lblEqSummary.Text = "設定: 本体メモリ保存済みのEQを使用中（変更は読込・貼付・詳細設定から）";
                return;
            }

            double peakWithPre;
            EqDspMath.ComputePeakFilterBoostDb(_eqProfile, out peakWithPre);
            bool eqClip = _chkEq != null && _chkEq.Checked && peakWithPre > 0.05;

            _lblEqSummary.ForeColor = eqClip ? Color.FromArgb(168, 24, 24) : SystemColors.ControlDarkDark;
            _lblEqSummary.Text = string.Format(
                CultureInfo.InvariantCulture,
                "設定: {0}  ({1}バンド / Preamp {2:+0.0;-0.0;0.0} dB / ピーク {3:+0.0;-0.0;0.0} dB)",
                _eqProfile.SourceLabel,
                _eqProfile.ActiveBandCount(),
                _eqProfile.PreampDb,
                peakWithPre);
        }

        private void QueueParamWrite()
        {
            if (!_deviceConnected) return;
            _pendingParamSend = true;
            _sendDebounceTimer.Stop();
            _sendDebounceTimer.Start();
        }

        private void OnSendDebounceTick(object sender, EventArgs e)
        {
            _sendDebounceTimer.Stop();
            FlushPendingParamWrite();
        }

        private void WriteCurrentParamsOrThrow()
        {
            _sendDebounceTimer.Stop();
            _pendingParamSend = false;
            _hid.WriteParams(
                _chkEq.Checked,
                (double)_numGame.Value,
                (double)_numMaster.Value,
                (double)_numPc.Value,
                _lastRxPinMode);
        }

        private void FlushPendingParamWrite()
        {
            if (!_pendingParamSend) return;
            _pendingParamSend = false;
            if (!_deviceConnected) return;

            try
            {
                _hid.WriteParams(
                    _chkEq.Checked,
                    (double)_numGame.Value,
                    (double)_numMaster.Value,
                    (double)_numPc.Value,
                    _lastRxPinMode);
                if (_statusText.ForeColor == SystemColors.ControlText)
                {
                    _statusText.Text = string.Format(
                        CultureInfo.InvariantCulture,
                        "反映済み (Master {0:0.0} dB / EQ {1})",
                        (double)_numMaster.Value,
                        _chkEq.Checked ? "ON" : "OFF");
                }
            }
            catch (Exception ex)
            {
                HandleCommFailure(ex);
            }
        }

        private void SyncEqFromDeviceIfSupported(MixerStatus st)
        {
            float devPre;
            float[][] devCoeffs;
            if (!_hid.TryReadDeviceEq(out devPre, out devCoeffs))
            {
                return;
            }
            _lastHwPreampLin = devPre;
            _lastHwCoeffs = devCoeffs;

            if (EqDspMath.CoeffsMatchProfile(_eqProfile, devPre, devCoeffs))
            {
                return;
            }

            if (EqDspMath.AreAllCoeffsFlat(devPre, devCoeffs) && !st.EqEnabled && !st.FlashConfigLoaded)
            {
                _eqProfile = new EqProfile();
                return;
            }

            EqProfile reconstructed;
            string lbl = st.FlashConfigLoaded ? "本体メモリ保存済み" : "本体の現在値";
            if (EqDspMath.TryReconstructProfileFromCoeffs(devPre, devCoeffs, lbl, out reconstructed))
            {
                _eqProfile = reconstructed;
                _hasLocalEqCache = true;
            }
        }

        public void RefreshFromDevice(bool manualAction)
        {
            try
            {
                MixerStatus st = _hid.ReadFullStatus();
                bool firstConnect = !_deviceConnected;
                _deviceConnected = true;
                _deviceFlashLoaded = st.FlashConfigLoaded;
                _lastRxPinMode = st.UartRxPinMode;
                _testToneActive = st.TestToneOn;

                if (firstConnect || manualAction)
                {
                    _lastPollClipCount = st.ClipCount;
                    if (manualAction)
                    {
                        _recentClipDetected = false;
                        _eqBypassLoudnessHint = false;
                    }
                    if (!_userDraggingSlider && !_pendingParamSend)
                    {
                        SyncEqFromDeviceIfSupported(st);
                    }
                }
                else if (st.ClipCount > _lastPollClipCount)
                {
                    _recentClipDetected = true;
                    _lastPollClipCount = st.ClipCount;
                }
                else
                {
                    _lastPollClipCount = st.ClipCount;
                }

                string winMuteNote = st.WindowsMuted ? " [Windowsミュート中]" : "";
                _lblConnState.Text = "状態: 接続済み" + winMuteNote;
                _lblConnState.ForeColor = SystemColors.ControlText;

                string swText = st.UartLocked ? "受信中" : "待機中";
                string obsText = st.ObsMicActive ? "動作中" : "待機中";
                string clipNote = _recentClipDetected ? "  |  [音割れ検知]" : "";
                _lblSignalDetail.Text = string.Format("Switch 2: {0}  |  OBS: {1}{2}", swText, obsText, clipNote);
                _lblSignalDetail.ForeColor = _recentClipDetected
                    ? Color.FromArgb(168, 20, 20)
                    : SystemColors.ControlDarkDark;

                if (!_userDraggingSlider && !_pendingParamSend)
                {
                    _syncingUi = true;
                    try
                    {
                        SetPairValue(_sldMaster, _numMaster, st.MasterDb);
                        SetPairValue(_sldGame, _numGame, st.GameDb);
                        SetPairValue(_sldPc, _numPc, st.PcTrimDb);
                        _chkEq.Checked = st.EqEnabled;
                        UpdateGainWarningsAndThresholds();
                        UpdateEqSummaryLabel();
                    }
                    finally
                    {
                        _syncingUi = false;
                    }
                }
                else
                {
                    UpdateGainWarningsAndThresholds();
                }

                _btnTestTone.Text = _testToneActive ? "テスト音: ON" : "テスト音: OFF";
                _btnSaveFlash.Enabled = true;

                if (firstConnect)
                {
                    SetUnsavedState(false);
                }
                if ((manualAction || firstConnect) && _statusText.ForeColor == SystemColors.ControlText)
                {
                    _statusText.Text = "接続済み";
                }
            }
            catch (Exception ex)
            {
                _deviceConnected = false;
                _deviceFlashLoaded = false;
                _recentClipDetected = false;
                _eqBypassLoudnessHint = false;
                _testToneActive = false;
                _lblConnState.Text = "状態: 未接続";
                _lblConnState.ForeColor = SystemColors.GrayText;
                _lblSignalDetail.Text = "Switch 2: ---  |  OBS: ---";
                _lblSignalDetail.ForeColor = SystemColors.ControlDarkDark;
                _btnTestTone.Text = "テスト音: OFF";
                _btnSaveFlash.Enabled = false;
                if (manualAction)
                {
                    _statusText.ForeColor = SystemColors.ControlText;
                    _statusText.Text = ex.Message;
                }
            }
        }

        private void HandleCommFailure(Exception ex)
        {
            _statusText.ForeColor = SystemColors.ControlText;
            _statusText.Text = "通信エラー: " + ex.Message;
            RefreshFromDevice(false);
        }

        private static void SetPairValue(GainSlider gs, NumericUpDown ud, double dbVal)
        {
            decimal dec = ClampDec(dbVal, ud.Minimum, ud.Maximum);
            ud.Value = dec;
            gs.SetValueSilent((double)dec);
        }

        private void OnEqCheckedChanged(object sender, EventArgs e)
        {
            if (_syncingUi) return;
            _recentClipDetected = false;
            _eqBypassLoudnessHint = (!_chkEq.Checked && _eqProfile != null && _eqProfile.PreampDb <= -8.0);
            UpdateEqSummaryLabel();
            UpdateGainWarningsAndThresholds();
            SetUnsavedState(true);
            QueueParamWrite();
        }

        private void ApplyEqProfileAndState(EqProfile profile, bool enableEq, bool showToastOnOffline)
        {
            bool wasEnabled = _chkEq.Checked;
            _recentClipDetected = false;
            _eqProfile = profile.Clone();
            SaveCachedEq(_eqProfile);
            _eqBypassLoudnessHint = (wasEnabled && !enableEq && _eqProfile != null && _eqProfile.PreampDb <= -8.0);

            _syncingUi = true;
            _chkEq.Checked = enableEq;
            _syncingUi = false;
            UpdateEqSummaryLabel();
            UpdateGainWarningsAndThresholds();
            SetUnsavedState(true);

            if (!_deviceConnected)
            {
                if (showToastOnOffline)
                {
                    MessageBox.Show(
                        this,
                        "ミキサーが未接続のため、編集内容のみ保持しました。",
                        "未接続",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Information);
                }
                return;
            }

            try
            {
                _hid.WriteEqProfile(_eqProfile);
                _hid.WriteParams(
                    _chkEq.Checked,
                    (double)_numGame.Value,
                    (double)_numMaster.Value,
                    (double)_numPc.Value,
                    _lastRxPinMode);
                CacheProfileCoeffsLocally(_eqProfile);
                if (_statusText.ForeColor == SystemColors.ControlText)
                {
                    _statusText.Text = string.Format("EQ反映済み: {0}", _eqProfile.SourceLabel);
                }
            }
            catch (Exception ex)
            {
                if (showToastOnOffline)
                {
                    MessageBox.Show(this, "EQ送信エラー: " + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
                HandleCommFailure(ex);
            }
        }

        private void OnLoadPeaceMainClick(object sender, EventArgs e)
        {
            using (OpenFileDialog dlg = new OpenFileDialog())
            {
                dlg.Title = "EQ設定ファイルを開く (.peace / .txt)";
                dlg.Filter = "Peace / Equalizer APO 設定 (*.peace;*.txt)|*.peace;*.txt|すべてのファイル (*.*)|*.*";
                if (!string.IsNullOrEmpty(_lastEqDir) && Directory.Exists(_lastEqDir))
                {
                    dlg.InitialDirectory = _lastEqDir;
                }
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    _lastEqDir = Path.GetDirectoryName(dlg.FileName);
                    EqProfile loaded = EqDspMath.ParsePeaceFile(dlg.FileName);
                    PromptAndApplyLoadedEq(loaded);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, ex.Message, "読み込みエラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void PromptAndApplyLoadedEq(EqProfile loaded)
        {
            bool nextEnable = _chkEq.Checked;
            if (!nextEnable)
            {
                DialogResult ans = MessageBox.Show(
                    this,
                    string.Format("「{0}」を読み込みました。\r\nイコライザーを有効にしますか？", loaded.SourceLabel),
                    "Biwamasu Mixer",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Question);
                if (ans == DialogResult.Yes) nextEnable = true;
            }
            ApplyEqProfileAndState(loaded, nextEnable, true);
        }

        private void OnOpenAiPasteMainClick(object sender, EventArgs e)
        {
            using (AiEqPasteForm dlg = new AiEqPasteForm(_appIcon))
            {
                if (dlg.ShowDialog(this) == DialogResult.OK && dlg.ParsedProfile != null)
                {
                    PromptAndApplyLoadedEq(dlg.ParsedProfile);
                }
            }
        }

        private void OnMainDragEnter(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop) || e.Data.GetDataPresent(DataFormats.UnicodeText))
            {
                e.Effect = DragDropEffects.Copy;
            }
            else
            {
                e.Effect = DragDropEffects.None;
            }
        }

        private void OnMainDragDrop(object sender, DragEventArgs e)
        {
            try
            {
                if (e.Data.GetDataPresent(DataFormats.FileDrop))
                {
                    string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
                    if (files != null && files.Length > 0)
                    {
                        _lastEqDir = Path.GetDirectoryName(files[0]);
                        PromptAndApplyLoadedEq(EqDspMath.ParsePeaceFile(files[0]));
                        return;
                    }
                }
                if (e.Data.GetDataPresent(DataFormats.UnicodeText))
                {
                    string txt = e.Data.GetData(DataFormats.UnicodeText) as string;
                    if (!string.IsNullOrEmpty(txt))
                    {
                        PromptAndApplyLoadedEq(EqDspMath.ParsePeaceText(txt, "AI・テキスト貼付"));
                    }
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "読み込みエラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Control && !e.Shift && e.KeyCode == Keys.V &&
                !(ActiveControl is NumericUpDown) && !(ActiveControl is TextBox))
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                try
                {
                    if (Clipboard.ContainsText())
                    {
                        EqProfile parsed = EqDspMath.ParsePeaceText(Clipboard.GetText(), "AI・テキスト貼付");
                        PromptAndApplyLoadedEq(parsed);
                        return;
                    }
                }
                catch { }
                OnOpenAiPasteMainClick(this, EventArgs.Empty);
                return;
            }
            base.OnKeyDown(e);
        }

        private FavoritePreset CaptureCurrentFavorite()
        {
            FavoritePreset fp = new FavoritePreset();
            string candidate = (_eqProfile != null && !string.IsNullOrEmpty(_eqProfile.SourceLabel) &&
                                _eqProfile.SourceLabel != "sample-peace.txt" && _eqProfile.SourceLabel != "前回の設定")
                ? Path.GetFileNameWithoutExtension(_eqProfile.SourceLabel)
                : "マイ設定";
            fp.Name = candidate;
            fp.MasterDb = (double)_numMaster.Value;
            fp.GameDb = (double)_numGame.Value;
            fp.PcTrimDb = (double)_numPc.Value;
            fp.EqEnabled = _chkEq.Checked;
            fp.Eq = _eqProfile.Clone();
            return fp;
        }

        private void ApplyFavoritePreset(FavoritePreset fp)
        {
            if (fp == null) return;
            _recentClipDetected = false;
            _syncingUi = true;
            try
            {
                SetPairValue(_sldMaster, _numMaster, fp.MasterDb);
                SetPairValue(_sldGame, _numGame, fp.GameDb);
                SetPairValue(_sldPc, _numPc, fp.PcTrimDb);
                _chkEq.Checked = fp.EqEnabled;
            }
            finally
            {
                _syncingUi = false;
            }
            UpdateGainWarningsAndThresholds();

            EqProfile eq = fp.Eq != null ? fp.Eq.Clone() : new EqProfile();
            eq.SourceLabel = fp.Name;
            ApplyEqProfileAndState(eq, fp.EqEnabled, true);
            if (_statusText.ForeColor == SystemColors.ControlText)
            {
                _statusText.Text = "お気に入り適用: " + fp.Name;
            }
        }

        private void OnAddFavoriteClick(object sender, EventArgs e)
        {
            FavoritePreset snap = CaptureCurrentFavorite();
            using (PromptNameDialog dlg = new PromptNameDialog(
                _appIcon,
                "お気に入りに追加",
                "保存する名前（イヤホン・ヘッドホン名や用途など）:",
                snap.Name,
                snap.FormatDetailText()))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;
                FavoritePreset existing = _favoriteStore.FindByName(dlg.EnteredName);
                if (existing != null)
                {
                    DialogResult ans = MessageBox.Show(
                        this,
                        "「" + dlg.EnteredName + "」は既に登録されています。上書きしますか？",
                        "上書き確認",
                        MessageBoxButtons.YesNo,
                        MessageBoxIcon.Question);
                    if (ans != DialogResult.Yes) return;
                    snap.FilePath = existing.FilePath;
                }
                snap.Name = dlg.EnteredName;
                try
                {
                    _favoriteStore.Save(snap);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "お気に入りの保存に失敗しました: " + ex.Message, "保存エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                _eqProfile.SourceLabel = snap.Name;
                UpdateEqSummaryLabel();
                if (_statusText.ForeColor == SystemColors.ControlText)
                {
                    _statusText.Text = "お気に入りに保存しました: " + snap.Name;
                }
            }
        }

        private void OnManageFavoritesClick(object sender, EventArgs e)
        {
            using (FavoriteManagerForm dlg = new FavoriteManagerForm(
                _appIcon,
                _favoriteStore,
                CaptureCurrentFavorite,
                ApplyFavoritePreset))
            {
                dlg.ShowDialog(this);
            }
        }

        private void OnExportPeaceMainClick(object sender, EventArgs e)
        {
            using (SaveFileDialog dlg = new SaveFileDialog())
            {
                dlg.Title = "EQ設定をファイルに保存";
                dlg.Filter = "Equalizer APO テキスト (*.txt)|*.txt";
                dlg.FileName = "my-biwamasu-eq.txt";
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                try
                {
                    EqDspMath.SavePeaceFile(dlg.FileName, _eqProfile);
                    if (_statusText.ForeColor == SystemColors.ControlText)
                    {
                        _statusText.Text = "ファイル保存完了: " + Path.GetFileName(dlg.FileName);
                    }
                }
                catch (Exception ex)
                {
                    MessageBox.Show(this, "保存に失敗しました: " + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }

        private void CacheProfileCoeffsLocally(EqProfile profile)
        {
            if (profile == null) return;
            string err;
            float pre = EqDspMath.ComputePreampLinear(profile.PreampDb, out err);
            if (err != null) return;
            float[][] coeffs = new float[11][];
            for (int i = 0; i < 11; i++)
            {
                coeffs[i] = EqDspMath.ComputeBiquad(profile.Bands[i], i + 1, out err);
                if (coeffs[i] == null) return;
            }
            _lastHwPreampLin = pre;
            _lastHwCoeffs = coeffs;
        }

        private void RestoreRawHardwareEqAndState(float rawPreamp, float[][] rawCoeffs, EqProfile fallbackProfile, bool enableEq)
        {
            _recentClipDetected = false;
            _eqBypassLoudnessHint = false;
            _eqProfile = fallbackProfile.Clone();
            SaveCachedEq(_eqProfile);

            _syncingUi = true;
            _chkEq.Checked = enableEq;
            _syncingUi = false;
            UpdateEqSummaryLabel();
            UpdateGainWarningsAndThresholds();

            if (!_deviceConnected) return;
            try
            {
                _hid.WriteRawEqCoeffs(rawPreamp, rawCoeffs);
                _hid.WriteParams(
                    _chkEq.Checked,
                    (double)_numGame.Value,
                    (double)_numMaster.Value,
                    (double)_numPc.Value,
                    _lastRxPinMode);
                _lastHwPreampLin = rawPreamp;
                _lastHwCoeffs = rawCoeffs;
            }
            catch (Exception ex)
            {
                HandleCommFailure(ex);
            }
        }

        private void OnOpenEqEditorClick(object sender, EventArgs e)
        {
            _pollTimer.Stop();
            try
            {
                if (_deviceConnected)
                {
                    float devPre;
                    float[][] devCoeffs;
                    if (_hid.TryReadDeviceEq(out devPre, out devCoeffs))
                    {
                        _lastHwPreampLin = devPre;
                        _lastHwCoeffs = devCoeffs;
                    }
                }

                using (EqEditorForm dlg = new EqEditorForm(
                    _eqProfile,
                    _chkEq.Checked,
                    _lastEqDir,
                    _appIcon,
                    delegate(EqProfile applied, bool eqOn, bool explicitCommit)
                    {
                        ApplyEqProfileAndState(applied, eqOn, explicitCommit);
                    },
                    _lastHwPreampLin,
                    _lastHwCoeffs,
                    RestoreRawHardwareEqAndState))
                {
                    dlg.ShowDialog(this);
                    if (!string.IsNullOrEmpty(dlg.LastSelectedEqDir))
                    {
                        _lastEqDir = dlg.LastSelectedEqDir;
                    }
                }
            }
            finally
            {
                _pollTimer.Start();
            }
        }

        private void OnToggleTestToneClick(object sender, EventArgs e)
        {
            if (!_deviceConnected)
            {
                MessageBox.Show(this, "Biwamasu Mixer が未接続です。", "未接続", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                bool next = !_testToneActive;
                _hid.SetTestTone(next);
                _testToneActive = next;
                _btnTestTone.Text = _testToneActive ? "テスト音: ON" : "テスト音: OFF";
                if (_statusText.ForeColor == SystemColors.ControlText)
                {
                    _statusText.Text = _testToneActive ? "テスト音再生中 (左440Hz / 右660Hz)" : "テスト音停止";
                }
            }
            catch (Exception ex)
            {
                HandleCommFailure(ex);
            }
        }

        private void OnResetDefaultsClick(object sender, EventArgs e)
        {
            if (!_deviceConnected)
            {
                MessageBox.Show(this, "Biwamasu Mixer が未接続です。", "未接続", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DialogResult ans = MessageBox.Show(
                this,
                "設定を初期値（-45.0 dB / EQ OFF）に戻しますか？",
                "初期値に戻す",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Question);
            if (ans != DialogResult.Yes) return;

            try
            {
                _recentClipDetected = false;
                _eqBypassLoudnessHint = false;
                _hid.ResetDefaults();
                _eqProfile = new EqProfile();
                SaveCachedEq(_eqProfile);
                RefreshFromDevice(false);
                UpdateEqSummaryLabel();
                SetUnsavedState(true);
                if (_statusText.ForeColor == SystemColors.ControlText)
                {
                    _statusText.Text = "初期値に戻しました";
                }
            }
            catch (Exception ex)
            {
                HandleCommFailure(ex);
            }
        }

        private void ExecuteVerifiedFlashSave()
        {
            if (_pendingParamSend)
            {
                WriteCurrentParamsOrThrow();
            }
            uint beforeSaves = _hid.ReadFullStatus().FlashSaves;
            _hid.SaveToFlash();

            MixerStatus after = null;
            Exception lastErr = null;
            for (int attempt = 0; attempt < 4; attempt++)
            {
                try
                {
                    after = _hid.ReadFullStatus();
                    if (after.FlashSaves > beforeSaves) break;
                }
                catch (Exception ex)
                {
                    lastErr = ex;
                }
                Thread.Sleep(60);
            }

            if (after == null)
            {
                throw lastErr ?? new IOException("保存後の状態確認に失敗しました。");
            }
            if (after.FlashSaves <= beforeSaves)
            {
                throw new IOException("本体フラッシュメモリの書き込み検証に失敗しました。");
            }

            SetUnsavedState(false);
            RefreshFromDevice(false);
        }

        private void OnSaveFlashClick(object sender, EventArgs e)
        {
            if (!_deviceConnected) return;
            try
            {
                ExecuteVerifiedFlashSave();
                if (_statusText.ForeColor == SystemColors.ControlText)
                {
                    _statusText.Text = "本体メモリに保存しました";
                }
                MessageBox.Show(
                    this,
                    "設定を本体メモリに保存しました。",
                    "Biwamasu Mixer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "保存に失敗しました: " + ex.Message, "エラー", MessageBoxButtons.OK, MessageBoxIcon.Error);
                HandleCommFailure(ex);
            }
        }

        private void OnEnterBootselClick(object sender, EventArgs e)
        {
            if (!_deviceConnected)
            {
                MessageBox.Show(this, "Biwamasu Mixer が未接続です。", "未接続", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DialogResult ans = MessageBox.Show(
                this,
                "BOOTSEL モード（ファームウェア更新用）に切り替えますか？\r\n音声出力が一時停止し、ドライブが開きます。",
                "BOOTSEL切替",
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (ans != DialogResult.Yes) return;

            try
            {
                _hid.EnterBootsel();
                SetUnsavedState(false);
                RefreshFromDevice(false);
                _statusText.Text = "BOOTSELモードへ切り替えました";
            }
            catch (Exception ex)
            {
                HandleCommFailure(ex);
            }
        }

        private static decimal ClampDec(double val, decimal min, decimal max)
        {
            decimal d = (decimal)val;
            if (d < min) return min;
            if (d > max) return max;
            return d;
        }
    }

    internal static class Program
    {
        [DllImport("user32.dll")]
        private static extern bool SetProcessDPIAware();

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        private static bool TryParseHexUshort(string raw, out ushort val)
        {
            val = 0;
            if (string.IsNullOrEmpty(raw)) return false;
            string clean = raw.Trim();
            if (clean.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(2);
            if (clean.EndsWith("u", StringComparison.OrdinalIgnoreCase)) clean = clean.Substring(0, clean.Length - 1);
            return ushort.TryParse(clean, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out val);
        }

        private static void ReadHeaderUsbIds(string baseDir, ref ushort vid, ref ushort pid)
        {
            string[] candidates = new string[]
            {
                Path.Combine(baseDir, "include", "usb_ids.h"),
                Path.Combine(baseDir, "..", "include", "usb_ids.h")
            };
            Regex vidRx = new Regex(@"#define\s+USB_VID\s+(0x[0-9a-fA-F]+u?)");
            Regex pidRx = new Regex(@"#define\s+MIXER_USB_PID\s+(0x[0-9a-fA-F]+u?)");

            for (int i = 0; i < candidates.Length; i++)
            {
                try
                {
                    string full = Path.GetFullPath(candidates[i]);
                    if (!File.Exists(full)) continue;
                    string txt = File.ReadAllText(full, Encoding.UTF8);
                    ushort hv = 0, hp = 0;
                    Match mv = vidRx.Match(txt);
                    Match mp = pidRx.Match(txt);
                    if (mv.Success) TryParseHexUshort(mv.Groups[1].Value, out hv);
                    if (mp.Success) TryParseHexUshort(mp.Groups[1].Value, out hp);
                    if (hv != 0 && hp != 0)
                    {
                        vid = hv;
                        pid = hp;
                        return;
                    }
                }
                catch { }
            }
        }

        private static int RunSelfTest(ushort vid, ushort pid)
        {
            EqProfile p = EqProfile.CreateBassBoostSample();
            string err;
            float pre = EqDspMath.ComputePreampLinear(p.PreampDb, out err);
            if (err != null || pre <= 0.0f) return 2;
            for (int i = 0; i < 11; i++)
            {
                float[] coeff = EqDspMath.ComputeBiquad(p.Bands[i], i + 1, out err);
                if (coeff == null || err != null) return 3;
            }

            double peakWithPre;
            double rawPeak = EqDspMath.ComputePeakFilterBoostDb(p, out peakWithPre);
            if (rawPeak <= 0.0 || peakWithPre > 0.2) return 4;

            string tmpTxt = Path.Combine(Path.GetTempPath(), "biwamasu-gui-selftest-" + Process.GetCurrentProcess().Id + ".txt");
            string tmpPeace = Path.Combine(Path.GetTempPath(), "biwamasu-gui-selftest-" + Process.GetCurrentProcess().Id + ".peace");
            string tmpFavDir = Path.Combine(Path.GetTempPath(), "biwamasu-gui-favtest-" + Process.GetCurrentProcess().Id);
            try
            {
                EqDspMath.SavePeaceFile(tmpTxt, p);
                EqProfile roundTrip = EqDspMath.ParsePeaceFile(tmpTxt);
                if (Math.Abs(roundTrip.PreampDb - p.PreampDb) > 0.01) return 5;
                if (Math.Abs(roundTrip.Bands[0].FrequencyHz - 100.0) > 0.01) return 6;

                // Test native .peace INI format (including [Frequencies1] per-speaker ignore & Filter=14/15)
                string iniContent =
                    "[Frequencies]\r\nFrequency1=105\r\nFrequency2=1000\r\nFrequency3=10000\r\n" +
                    "[Gains]\r\nGain1=-5.5\r\nGain3=2.5\r\n" +
                    "[Qualities]\r\nQuality1=0.7\r\nQuality2=1.41\r\nQuality3=0.7\r\n" +
                    "[Filters]\r\nFilter1=14\r\nFilter3=15\r\n" +
                    "[Frequencies1]\r\nFrequency1=9999\r\n" +
                    "[General]\r\nPreAmp=-3\r\n";
                File.WriteAllText(tmpPeace, iniContent, new UTF8Encoding(false));
                EqProfile iniParsed = EqDspMath.ParsePeaceFile(tmpPeace);
                if (Math.Abs(iniParsed.PreampDb - (-3.0)) > 0.01) return 8;
                if (iniParsed.Bands[0].Kind != FilterKind.LSC || Math.Abs(iniParsed.Bands[0].FrequencyHz - 105.0) > 0.01) return 9;
                if (iniParsed.Bands[1].Kind != FilterKind.PK || Math.Abs(iniParsed.Bands[1].GainDb) > 0.01) return 10;
                if (iniParsed.Bands[2].Kind != FilterKind.HSC || Math.Abs(iniParsed.Bands[2].GainDb - 2.5) > 0.01) return 11;

                // Test messy AI chat output paste parsing (# Preset, markdown fences, bold Preamp, Unicode minus, Filter:/Filter1:, BW Oct, comma Fc)
                string messyAiReply =
                    "はい、足音重視のイコライザー設定を作成しました！\r\n```text\r\n" +
                    "# Preset: FPS足音カスタム\r\n" +
                    "**Preamp：** −4.2 dB\r\n" +
                    "- Filter1: ON LSC Fc 100 Hz Gain −2.5 dB Q 0.71\r\n" +
                    "- Filter: ON PK Fc 2,000 Hz Gain 4.0 dB BW Oct 1.0\r\n" +
                    "```\r\nこのブロックをコピーして貼り付けてください。";
                EqProfile aiParsed = EqDspMath.ParsePeaceText(messyAiReply, "fallback");
                if (aiParsed.SourceLabel != "FPS足音カスタム" || Math.Abs(aiParsed.PreampDb - (-4.2)) > 0.01) return 13;
                if (Math.Abs(aiParsed.Bands[1].FrequencyHz - 2000.0) > 0.01 || Math.Abs(aiParsed.Bands[1].GainDb - 4.0) > 0.01) return 14;
                if (Math.Abs(aiParsed.Bands[1].Q - 1.41) > 0.03) return 17;

                // Test rejection of unsupported active filters, per-speaker Channel, NaN, and decimal comma
                string[] mustReject = new string[]
                {
                    "Preamp: -6 dB\r\nFilter 1: ON HP Fc 80 Hz",
                    "Channel: L\r\nFilter 1: ON PK Fc 1000 Hz Gain 3 dB Q 1.41",
                    "GraphicEQ: 25 0; 40 0",
                    "Preamp: -6,5 dB\r\nFilter 1: ON PK Fc 1000 Hz Gain 3 dB Q 1.41",
                    "Filter 1: ON PK Fc 1000,5 Hz Gain 3 dB Q 1.41",
                    "[Frequencies]\r\nFrequency1=1000\r\n[Gains]\r\nGain1=3\r\n[General]\r\nPreAmp=NaN\r\n"
                };
                for (int r = 0; r < mustReject.Length; r++)
                {
                    bool rejected = false;
                    try { EqDspMath.ParsePeaceText(mustReject[r], "bad"); }
                    catch (InvalidDataException) { rejected = true; }
                    if (!rejected) return 18;
                }

                // Test RBJ biquad reconstruction round-trip (LSC + PK + HSC, positive & negative gains)
                EqProfile origSample = EqProfile.CreateVoiceFootstepSample();
                float origPre = EqDspMath.ComputePreampLinear(origSample.PreampDb, out err);
                float[][] origCoeffs = new float[11][];
                for (int i = 0; i < 11; i++)
                {
                    origCoeffs[i] = EqDspMath.ComputeBiquad(origSample.Bands[i], i + 1, out err);
                    if (origCoeffs[i] == null) return 19;
                }
                EqProfile recon;
                if (!EqDspMath.TryReconstructProfileFromCoeffs(origPre, origCoeffs, "本体メモリ保存済み", out recon)) return 20;
                if (Math.Abs(recon.PreampDb - origSample.PreampDb) > 0.11) return 21;
                if (recon.Bands[0].Kind != FilterKind.LSC || Math.Abs(recon.Bands[0].GainDb - (-3.0)) > 0.15 || Math.Abs(recon.Bands[0].FrequencyHz - 100.0) > 1.5) return 22;
                if (recon.Bands[5].Kind != FilterKind.PK || Math.Abs(recon.Bands[5].GainDb - 3.0) > 0.15 || Math.Abs(recon.Bands[5].FrequencyHz - 2000.0) > 5.0) return 23;
                if (recon.Bands[10].Kind != FilterKind.HSC || Math.Abs(recon.Bands[10].GainDb - (-1.5)) > 0.15 || Math.Abs(recon.Bands[10].FrequencyHz - 10000.0) > 30.0) return 24;

                // Test FavoritePresetStore save / list / delete (including reserved name CON)
                FavoritePresetStore store = new FavoritePresetStore(tmpFavDir);
                FavoritePreset fav = new FavoritePreset();
                fav.Name = "CON";
                fav.MasterDb = -43.5;
                fav.GameDb = -2.0;
                fav.PcTrimDb = -1.0;
                fav.EqEnabled = true;
                fav.Eq = aiParsed;
                store.Save(fav);
                List<FavoritePreset> listed = store.ListAll();
                if (listed.Count != 1 || Math.Abs(listed[0].MasterDb - (-43.5)) > 0.01 || !listed[0].EqEnabled) return 15;
                store.Delete(listed[0]);
                if (store.ListAll().Count != 0) return 16;
            }
            finally
            {
                if (File.Exists(tmpTxt)) try { File.Delete(tmpTxt); } catch { }
                if (File.Exists(tmpPeace)) try { File.Delete(tmpPeace); } catch { }
                if (Directory.Exists(tmpFavDir)) try { Directory.Delete(tmpFavDir, true); } catch { }
            }

            using (Icon ic = AppIconFactory.CreateAppIcon())
            {
                if (ic == null || ic.Width <= 0 || ic.Height <= 0) return 12;
            }

            if (!GainSlider.VerifyGrooveDoubleClickSafetyForTest()) return 26;

            using (MainForm mf = new MainForm(vid != 0 ? vid : (ushort)0xcafe, pid != 0 ? pid : (ushort)0x4026))
            using (EqEditorForm ef = new EqEditorForm(p, true, AppDomain.CurrentDomain.BaseDirectory, null, null))
            using (HelpGuideForm hf = new HelpGuideForm(null, 0))
            using (AiEqPasteForm af = new AiEqPasteForm(null, false))
            {
                if (!mf.VerifyEqBypassHintForTest()) return 25;
                mf.SetPreviewLevelsForTest(-11.0, 11.5, 0.0);
                IntPtr h1 = mf.Handle;
                IntPtr h2 = ef.Handle;
                IntPtr h3 = hf.Handle;
                IntPtr h4 = af.Handle;
                if (h1 == IntPtr.Zero || h2 == IntPtr.Zero || h3 == IntPtr.Zero || h4 == IntPtr.Zero) return 7;
            }
            return 0;
        }

        [STAThread]
        private static int Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            ushort vid = BuildConfig.DefaultVid;
            ushort pid = BuildConfig.DefaultMixerPid;
            ReadHeaderUsbIds(AppDomain.CurrentDomain.BaseDirectory, ref vid, ref pid);

            bool devIds = false;
            bool explicitVid = false;
            bool explicitPid = false;
            bool selfTest = false;
            string writeIcoPath = null;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (string.Equals(a, "--dev-test-ids", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(a, "-DevTestIds", StringComparison.OrdinalIgnoreCase))
                {
                    devIds = true;
                }
                else if (string.Equals(a, "--self-test", StringComparison.OrdinalIgnoreCase))
                {
                    selfTest = true;
                }
                else if (string.Equals(a, "--write-ico", StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                {
                    writeIcoPath = args[++i];
                }
                else if ((string.Equals(a, "--vid", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(a, "-UsbVid", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    ushort parsed;
                    if (TryParseHexUshort(args[++i], out parsed)) { vid = parsed; explicitVid = true; }
                }
                else if ((string.Equals(a, "--pid", StringComparison.OrdinalIgnoreCase) ||
                          string.Equals(a, "-UsbPid", StringComparison.OrdinalIgnoreCase)) && i + 1 < args.Length)
                {
                    ushort parsed;
                    if (TryParseHexUshort(args[++i], out parsed)) { pid = parsed; explicitPid = true; }
                }
            }

            if (!string.IsNullOrEmpty(writeIcoPath))
            {
                byte[] icoBytes = AppIconFactory.BuildFishEmojiIcoBytes();
                File.WriteAllBytes(writeIcoPath, icoBytes);
                return 0;
            }

            if (devIds)
            {
                if (!explicitVid) vid = 0xcafe;
                if (!explicitPid) pid = 0x4026;
            }

            if (selfTest)
            {
                return RunSelfTest(vid, pid);
            }

            if (vid == 0 || pid == 0)
            {
                MessageBox.Show(
                    "USB VID/PID が未設定です (0x0000)。\r\n" +
                    "include/usb_ids.h を設定してビルドするか、--dev-test-ids を指定してください。",
                    "Biwamasu Mixer",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                return 1;
            }

            bool createdNew = true;
            string mutexName = string.Format(CultureInfo.InvariantCulture, "Local\\BiwamasuMixerSettingTool_{0:X4}_{1:X4}", vid, pid);
            using (Mutex singleInstanceMutex = new Mutex(true, mutexName, out createdNew))
            {
                if (!createdNew)
                {
                    IntPtr existingHwnd = FindWindow(null, "Biwamasu-Mixer Setting tool");
                    if (existingHwnd != IntPtr.Zero)
                    {
                        if (IsIconic(existingHwnd)) ShowWindow(existingHwnd, 9);
                        SetForegroundWindow(existingHwnd);
                    }
                    return 0;
                }

                Application.Run(new MainForm(vid, pid));
            }
            return 0;
        }
    }
}

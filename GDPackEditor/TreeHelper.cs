using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace GDPackEditor
{
    public static class TreeHelper
    {
        // --- 系统文件夹图标（shell32.dll）---
        [DllImport("shell32.dll", CharSet = CharSet.Auto)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, ref SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private const uint SHGFI_ICON = 0x100;
        private const uint SHGFI_SMALLICON = 0x1;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x10;
        private const uint FILE_ATTRIBUTE_DIRECTORY = 0x10;

        public static ImageList CreateSystemFolderIcons(int iconSize)
        {
            var imageList = new ImageList
            {
                ImageSize = new Size(iconSize, iconSize),
                ColorDepth = ColorDepth.Depth32Bit
            };

            var shfi = new SHFILEINFO();
            uint size = (uint)Marshal.SizeOf<SHFILEINFO>();

            SHGetFileInfo("dummy", FILE_ATTRIBUTE_DIRECTORY, ref shfi, size,
                SHGFI_ICON | SHGFI_SMALLICON | SHGFI_USEFILEATTRIBUTES);
            if (shfi.hIcon != IntPtr.Zero)
            {
                using (var icon = Icon.FromHandle(shfi.hIcon))
                    imageList.Images.Add(icon.ToBitmap());
                DestroyIcon(shfi.hIcon);
            }

            using (var src = (Bitmap)imageList.Images[0].Clone())
            {
                var openBmp = new Bitmap(src.Width, src.Height);
                using (var g = Graphics.FromImage(openBmp))
                {
                    g.DrawImage(src, 0, 0);
                    using var overlay = new SolidBrush(Color.FromArgb(55, 255, 255, 200));
                    g.FillRectangle(overlay, 0, 0, openBmp.Width, openBmp.Height);
                }
                imageList.Images.Add(openBmp);
            }

            return imageList;
        }
    }
}

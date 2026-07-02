using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;

namespace BIManage.Addons.Helpers
{
    public static class IconHelper
    {
        private const string ICON_RESOURCE_NAME = "BIManage.Addons.Resources.Icons.BIManage.png";

        public static BitmapImage GetLogoBitmapImage()
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                Stream stream = assembly.GetManifestResourceStream(ICON_RESOURCE_NAME)
                    ?? assembly.GetManifestResourceStream("BIManage.Addons.Resources.BIManage.png")
                    ?? assembly.GetManifestResourceStream("BIManage.Addons.BIManage.png");

                if (stream != null)
                {
                    using (stream)
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.StreamSource = stream;
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        return bitmap;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load logo: {ex.Message}");
            }
            return null;
        }

        public static BitmapImage GetAppIconBitmap() => GetLogoBitmapImage();

        public static BitmapImage LoadEmbeddedBitmap(string resourceName)
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                using (Stream stream = assembly.GetManifestResourceStream(resourceName))
                {
                    if (stream != null)
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.StreamSource = stream;
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        return bitmap;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load icon '{resourceName}': {ex.Message}");
            }
            return null;
        }

        public static void SetWindowIcon(Window window)
        {
            try
            {
                Assembly assembly = Assembly.GetExecutingAssembly();
                Stream stream = assembly.GetManifestResourceStream(ICON_RESOURCE_NAME)
                    ?? assembly.GetManifestResourceStream("BIManage.Addons.Resources.BIManage.png")
                    ?? assembly.GetManifestResourceStream("BIManage.Addons.BIManage.png");

                if (stream != null)
                {
                    using (stream)
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.StreamSource = stream;
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        window.Icon = bitmap;
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Failed to load window icon: {ex.Message}");
            }
        }
    }
}

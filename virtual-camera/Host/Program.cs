using System;
using System.Threading;
using DirectN;
using VCamNetSampleSource;

internal static class Program
{
    [MTAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0])) return 2;
        try
        {
            MFFunctions.MFStartup();
            try
            {
                Functions.MFCreateVirtualCamera(
                    __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0001.MFVirtualCameraType_SoftwareCameraSource,
                    __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0002.MFVirtualCameraLifetime_Session,
                    __MIDL___MIDL_itf_mfvirtualcamera_0000_0000_0003.MFVirtualCameraAccess_CurrentUser,
                    args[0], "{" + Shared.CLSID_VCamNet + "}", null, 0, out var camera).ThrowOnError();
                using var source = new ComObject<IMFVirtualCamera>(camera);
                source.Object.Start(null).ThrowOnError();
                Console.WriteLine("READY");
                Console.ReadLine();
                source.Object.Remove().ThrowOnError();
                return 0;
            }
            finally { MFFunctions.MFShutdown(); }
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }
}


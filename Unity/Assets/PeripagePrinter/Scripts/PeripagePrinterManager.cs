using System;
using UnityEngine;
using UnityEngine.Android;

/// <summary>
/// Attach this to a GameObject named "PeripageManager" in your kiosk scene
/// (the name must match what you pass into PeripageBridge.init(), and what
/// UnitySendMessage targets from the Kotlin side).
///
/// Handles: runtime permissions, connecting to the printer, converting a
/// Texture2D into the 1-bit packed bitmap format the printer needs, and
/// firing the print call.
/// </summary>
public class PeripagePrinterManager : MonoBehaviour
{
    public static PeripagePrinterManager Instance { get; private set; }

    [Header("Printer")]
    [Tooltip("Bluetooth MAC address of the kiosk's paired Peripage printer. " +
             "Fixed for a kiosk since it's always the same physical unit.")]
    public string printerMacAddress = "AA:BB:CC:DD:EE:FF";

    public const int PRINTER_WIDTH_PX = 384;

    public event Action OnConnected;
    public event Action<string> OnConnectFailed;
    public event Action OnPrintComplete;
    public event Action<string> OnPrintFailed;

    private AndroidJavaObject _bridge;
    private bool _initialized;

    void Awake()
    {
        if (Instance != null) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);
    }

    void Start()
    {
        RequestBluetoothPermissions();
    }

    private void RequestBluetoothPermissions()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        if (!Permission.HasUserAuthorizedPermission("android.permission.BLUETOOTH_CONNECT"))
        {
            Permission.RequestUserPermission("android.permission.BLUETOOTH_CONNECT");
        }
        if (!Permission.HasUserAuthorizedPermission("android.permission.BLUETOOTH_SCAN"))
        {
            Permission.RequestUserPermission("android.permission.BLUETOOTH_SCAN");
        }
        // On a kiosk, permissions can also just be granted once at setup time
        // via adb, so you don't have to handle the prompt UI at runtime:
        //   adb shell pm grant <package> android.permission.BLUETOOTH_CONNECT
        //   adb shell pm grant <package> android.permission.BLUETOOTH_SCAN
#endif
        InitBridge();
    }

    private void InitBridge()
    {
#if UNITY_ANDROID && !UNITY_EDITOR
        using (var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer"))
        {
            var activity = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            _bridge = new AndroidJavaObject("com.kiosk.peripage.PeripageBridge", activity);
            _bridge.Call("init", gameObject.name);
            _initialized = true;
        }
#endif
    }

    public void Connect()
    {
        if (!_initialized) { Debug.LogWarning("Bridge not initialized yet"); return; }
        _bridge.Call("connect", printerMacAddress);
    }

    public void Disconnect()
    {
        if (!_initialized) return;
        _bridge.Call("disconnect");
    }

    public bool IsConnected()
    {
        if (!_initialized) return false;
        return _bridge.Call<bool>("isConnected");
    }

    /// <summary>
    /// Converts a Texture2D (e.g. the kiosk photo) into the packed 1-bit
    /// bitmap format and sends it to the printer. Handles resizing to the
    /// printer's native width and basic thresholding to monochrome.
    /// </summary>
    public void PrintPhoto(Texture2D source)
    {
        if (!_initialized || _bridge == null)
        {
            OnPrintFailed?.Invoke("Bridge not initialized");
            return;
        }

        Texture2D resized = ResizeToPrinterWidth(source, PRINTER_WIDTH_PX);
        byte[] packed = ToPackedMonochrome(resized, out int height);

        _bridge.Call("printBitmap", packed, PRINTER_WIDTH_PX, height);

        if (resized != source) Destroy(resized);
    }

    // ---------- Image conversion ----------

    private Texture2D ResizeToPrinterWidth(Texture2D source, int targetWidth)
    {
        if (source.width == targetWidth) return source;

        int targetHeight = Mathf.RoundToInt(source.height * (targetWidth / (float)source.width));
        RenderTexture rt = RenderTexture.GetTemporary(targetWidth, targetHeight);
        Graphics.Blit(source, rt);

        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D resized = new Texture2D(targetWidth, targetHeight, TextureFormat.RGBA32, false);
        resized.ReadPixels(new Rect(0, 0, targetWidth, targetHeight), 0, 0);
        resized.Apply();
        RenderTexture.active = prev;
        RenderTexture.ReleaseTemporary(rt);

        return resized;
    }

    /// <summary>
    /// Converts to 1-bit packed rows (MSB-first), using simple luminance
    /// thresholding. Swap in Floyd–Steinberg dithering later for noticeably
    /// better photo quality on thermal output.
    /// </summary>
    private byte[] ToPackedMonochrome(Texture2D tex, out int height)
    {
        int width = tex.width;
        height = tex.height;
        Color32[] pixels = tex.GetPixels32();

        int bytesPerRow = Mathf.CeilToInt(width / 8f);
        byte[] packed = new byte[bytesPerRow * height];

        const float threshold = 0.5f;

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Texture2D pixels are bottom-up; flip so row 0 = top of image.
                Color32 c = pixels[(height - 1 - y) * width + x];
                float luminance = (0.299f * c.r + 0.587f * c.g + 0.114f * c.b) / 255f;
                bool isBlack = luminance < threshold;

                if (isBlack)
                {
                    int byteIndex = y * bytesPerRow + (x / 8);
                    int bitIndex = 7 - (x % 8);
                    packed[byteIndex] |= (byte)(1 << bitIndex);
                }
            }
        }

        return packed;
    }

    // ---------- Callbacks invoked by Kotlin via UnitySendMessage ----------
    // Method names below must exactly match what PeripageBridge.sendToUnity() calls.

    public void OnConnectedCallback(string macAddress)
    {
        Debug.Log($"Printer connected: {macAddress}");
        OnConnected?.Invoke();
    }

    public void OnConnectFailedCallback(string error)
    {
        Debug.LogWarning($"Printer connect failed: {error}");
        OnConnectFailed?.Invoke(error);
    }

    public void OnDisconnectedCallback(string _)
    {
        Debug.Log("Printer disconnected");
    }

    public void OnPrintCompleteCallback(string _)
    {
        Debug.Log("Print complete");
        OnPrintComplete?.Invoke();
    }

    public void OnPrintFailedCallback(string error)
    {
        Debug.LogWarning($"Print failed: {error}");
        OnPrintFailed?.Invoke(error);
    }
}

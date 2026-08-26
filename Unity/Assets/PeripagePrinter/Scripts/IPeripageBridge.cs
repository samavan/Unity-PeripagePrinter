/// <summary>
/// Common interface implemented by both the real Android Bluetooth bridge
/// and the Editor mock, so PeripagePrinterManager can talk to either
/// without caring which platform it's running on.
/// </summary>
public interface IPeripageBridge
{
    void Connect(string macAddress);
    void Disconnect();
    bool IsConnected();
    void PrintBitmap(byte[] imageBytes);
}

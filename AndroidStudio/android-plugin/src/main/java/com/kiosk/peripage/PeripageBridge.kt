package com.kiosk.peripage

import android.annotation.SuppressLint
import android.app.Activity
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothSocket
import android.util.Log
import java.io.OutputStream
import java.util.UUID
import java.util.concurrent.Executors

/**
 * PeripageBridge
 *
 * Thin wrapper around Android Bluetooth Classic (SPP) that Unity calls into
 * via AndroidJavaObject. Designed for a kiosk use-case: one known printer,
 * long-lived connection, simple connect/print/disconnect calls.
 *
 * Unity calls these methods with UnitySendMessage callbacks back to a
 * GameObject name + method name you specify in init().
 */
class PeripageBridge(private val activity: Activity) {

    companion object {
        private const val TAG = "PeripageBridge"
        // Standard SPP UUID used by most Bluetooth Classic serial devices,
        // including Peripage printers.
        private val SPP_UUID: UUID = UUID.fromString("00001101-0000-1000-8000-00805F9B34FB")

        // Printer native resolution (P21 uses 384px wide thermal head, standard
        // across most Peripage models — confirm against your unit if unsure).
        const val PRINTER_WIDTH_PX = 384
    }

    private var socket: BluetoothSocket? = null
    private var outputStream: OutputStream? = null
    private val executor = Executors.newSingleThreadExecutor()

    private var unityGameObjectName: String = "PeripageManager"

    fun init(unityGameObjectName: String) {
        this.unityGameObjectName = unityGameObjectName
    }

    // ---------- Discovery ----------

    @SuppressLint("MissingPermission")
    fun getPairedPrinters(): Array<String> {
        // Returns "Name|MacAddress" strings so Unity can list + let the
        // kiosk operator (or auto-select logic) pick the right device.
        val adapter = BluetoothAdapter.getDefaultAdapter() ?: return emptyArray()
        val paired = adapter.bondedDevices ?: return emptyArray()
        return paired
            .filter { it.name?.contains("PeriPage", ignoreCase = true) == true
                    || it.name?.contains("PT-", ignoreCase = true) == true }
            .map { "${it.name}|${it.address}" }
            .toTypedArray()
    }

    // ---------- Connection ----------

    @SuppressLint("MissingPermission")
    fun connect(macAddress: String) {
        executor.execute {
            try {
                val adapter = BluetoothAdapter.getDefaultAdapter()
                val device: BluetoothDevice = adapter.getRemoteDevice(macAddress)

                // Cancel discovery, it slows down the connection attempt.
                adapter.cancelDiscovery()

                val sock = device.createRfcommSocketToServiceRecord(SPP_UUID)
                sock.connect()

                socket = sock
                outputStream = sock.outputStream

                sendToUnity("OnConnected", macAddress)
            } catch (e: Exception) {
                Log.e(TAG, "Connect failed", e)
                sendToUnity("OnConnectFailed", e.message ?: "unknown error")
            }
        }
    }

    fun disconnect() {
        executor.execute {
            try {
                outputStream?.close()
                socket?.close()
            } catch (e: Exception) {
                Log.e(TAG, "Disconnect error", e)
            } finally {
                outputStream = null
                socket = null
                sendToUnity("OnDisconnected", "")
            }
        }
    }

    fun isConnected(): Boolean {
        return socket?.isConnected == true
    }

    // ---------- Printing ----------

    /**
     * Prints a 1-bit monochrome bitmap.
     *
     * @param packedBits byte array where each bit represents one pixel
     *        (1 = black/print, 0 = white), packed MSB-first, row-major,
     *        width MUST equal PRINTER_WIDTH_PX (384).
     * @param widthPx width of the image in pixels (must be 384)
     * @param heightPx height of the image in pixels
     *
     * NOTE: The actual Peripage command protocol (init sequence, row-feed
     * commands, etc.) is proprietary and reverse-engineered by the community.
     * Rather than re-deriving it here, integrate the command set from:
     *   https://github.com/Dibyakshu/peripage-kotlin-bluetooth-printer
     *   https://github.com/bitrate16/peripage-python (protocol reference)
     * and drop the equivalent "print raster" byte sequence into
     * writeRasterCommand() below.
     */
    fun printBitmap(packedBits: ByteArray, widthPx: Int, heightPx: Int) {
        executor.execute {
            val os = outputStream
            if (os == null) {
                sendToUnity("OnPrintFailed", "not connected")
                return@execute
            }
            try {
                if (widthPx != PRINTER_WIDTH_PX) {
                    sendToUnity("OnPrintFailed", "width must be $PRINTER_WIDTH_PX px")
                    return@execute
                }
                writeRasterCommand(os, packedBits, widthPx, heightPx)
                os.flush()
                sendToUnity("OnPrintComplete", "")
            } catch (e: Exception) {
                Log.e(TAG, "Print failed", e)
                sendToUnity("OnPrintFailed", e.message ?: "unknown error")
            }
        }
    }

    /**
     * Placeholder for the actual Peripage raster/print command protocol.
     * Replace body with the real init + row commands from the reference
     * libraries linked above. Left explicit (not hidden) so it's obvious
     * this is the one part you must port over before this compiles into
     * a working printer call.
     */
    private fun writeRasterCommand(
        os: OutputStream,
        packedBits: ByteArray,
        widthPx: Int,
        heightPx: Int
    ) {
        TODO(
            "Port the Peripage init + raster-row command sequence here " +
            "from the reference Kotlin/Python libraries. This class only " +
            "handles the Bluetooth transport, not the print protocol."
        )
    }

    // ---------- Unity messaging ----------

    private fun sendToUnity(method: String, message: String) {
        try {
            val unityPlayerClass = Class.forName("com.unity3d.player.UnityPlayer")
            val sendMessage = unityPlayerClass.getMethod(
                "UnitySendMessage", String::class.java, String::class.java, String::class.java
            )
            sendMessage.invoke(null, unityGameObjectName, method, message)
        } catch (e: Exception) {
            Log.e(TAG, "Failed to send message to Unity", e)
        }
    }
}

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
 * This class is transport ONLY — it knows nothing about the Peripage print
 * protocol (framing, dithering, bitmap packing). All of that lives in the
 * shared C# PeripageBitmapProtocol class on the Unity side, which builds a
 * fully-framed byte array and hands it to printRaw() below to be written
 * straight over the Bluetooth socket. Keeping it this way means the protocol
 * only has to be implemented/maintained once, and it stays identical across
 * Android and Mac.
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
     * Writes an already-fully-framed print packet straight to the Bluetooth
     * socket. The bytes passed in already contain the Peripage protocol's
     * prefix, padding, raster header, packed bitmap data, and footer — all
     * built on the Unity/C# side by PeripageBitmapProtocol +
     * PeripageAndroidBridge, so this method never needs to know the printer
     * command format.
     *
     * Bluetooth Classic SPP (RFCOMM) is a genuine byte stream, not a
     * packetized transport like BLE GATT, so writing the whole payload in
     * one call is safe — no row-by-row chunking or inter-chunk delay needed
     * here (unlike the Mac BLE bridge).
     */
    fun printRaw(bytes: ByteArray) {
        executor.execute {
            val os = outputStream
            if (os == null) {
                sendToUnity("OnPrintFailed", "not connected")
                return@execute
            }
            try {
                os.write(bytes)
                os.flush()
                sendToUnity("OnPrintComplete", "")
            } catch (e: Exception) {
                Log.e(TAG, "Print failed", e)
                sendToUnity("OnPrintFailed", e.message ?: "unknown error")
            }
        }
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
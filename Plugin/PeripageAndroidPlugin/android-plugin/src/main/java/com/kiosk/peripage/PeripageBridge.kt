package com.kiosk.peripage

import android.annotation.SuppressLint
import android.app.Activity
import android.bluetooth.BluetoothAdapter
import android.bluetooth.BluetoothDevice
import android.bluetooth.BluetoothSocket
import android.os.Build
import android.util.Log
import java.io.OutputStream
import java.util.UUID
import java.util.concurrent.Executors
import java.util.concurrent.atomic.AtomicBoolean

/**
 * PeripageBridge
 *
 * Thin wrapper around Android Bluetooth Classic (SPP) that Unity calls into
 * via AndroidJavaObject. Transport ONLY: the Peripage print protocol lives in
 * the C# PeripageBitmapProtocol class, which hands us a fully-framed byte
 * array via printRaw().
 *
 * IMPORTANT: the method names passed to UnitySendMessage below must match the
 * public methods on PeripagePrinterManager.cs EXACTLY (they end in "Callback").
 * If they don't, Unity silently drops the message and nothing shows up in
 * IngameDebugConsole.
 */
class PeripageBridge(private val activity: Activity) {

    companion object {
        private const val TAG = "PeripageBridge"
        private val SPP_UUID: UUID = UUID.fromString("00001101-0000-1000-8000-00805F9B34FB")

        // Must match the method names in PeripagePrinterManager.cs (#region Native Callbacks)
        private const val CB_CONNECTED = "OnConnectedCallback"
        private const val CB_CONNECT_FAILED = "OnConnectFailedCallback"
        private const val CB_DISCONNECTED = "OnDisconnectedCallback"
        private const val CB_PRINT_COMPLETE = "OnPrintCompleteCallback"
        private const val CB_PRINT_FAILED = "OnPrintFailedCallback"
        private const val CB_NATIVE_LOG = "OnNativeLogCallback"
    }

    @Volatile private var socket: BluetoothSocket? = null
    @Volatile private var outputStream: OutputStream? = null
    private val executor = Executors.newSingleThreadExecutor()

    // Guards against a second connect() (e.g. OK pressed twice) piling up
    // behind a first attempt that is still running.
    private val connecting = AtomicBoolean(false)

    private var unityGameObjectName: String = "PeripageManager"

    fun init(unityGameObjectName: String) {
        this.unityGameObjectName = unityGameObjectName
        log("init('$unityGameObjectName'), Android SDK ${Build.VERSION.SDK_INT}")
    }

    // ---------- Logging (forwarded to Unity so IngameDebugConsole can show it) ----------

    private fun log(msg: String) {
        Log.d(TAG, msg)
        sendToUnity(CB_NATIVE_LOG, msg)
    }

    // ---------- Diagnostics ----------

    @SuppressLint("MissingPermission")
    private fun describeAdapter(adapter: BluetoothAdapter): String =
        "enabled=${adapter.isEnabled}, state=${adapter.state} (12=ON), " +
            "discovering=${adapter.isDiscovering}, name=${adapter.name}"

    /**
     * What kind of Bluetooth device is this, and which services did it
     * advertise when it was paired? Tells us whether the printer even offers
     * classic SPP (UUID 00001101-...), or is BLE-only.
     */
    @SuppressLint("MissingPermission")
    private fun describeDevice(device: BluetoothDevice): String {
        val typeName = when (device.type) {
            BluetoothDevice.DEVICE_TYPE_CLASSIC -> "CLASSIC"
            BluetoothDevice.DEVICE_TYPE_LE -> "LE-only"
            BluetoothDevice.DEVICE_TYPE_DUAL -> "DUAL"
            else -> "UNKNOWN"
        }
        val cls = device.bluetoothClass?.let {
            "major=0x${Integer.toHexString(it.majorDeviceClass)} device=0x${Integer.toHexString(it.deviceClass)}"
        } ?: "none"
        val uuids = device.uuids?.joinToString { it.uuid.toString() } ?: "none cached"
        return "type=$typeName, class=$cls, uuids=[$uuids]"
    }

    // ---------- Discovery ----------

    @SuppressLint("MissingPermission")
    fun getPairedPrinters(): Array<String> {
        // Returns "Name|MacAddress" strings. Filter widened to include "PPG"
        // (e.g. PPG_P21_916F), which the old filter excluded.
        val adapter = BluetoothAdapter.getDefaultAdapter() ?: return emptyArray()
        val paired = adapter.bondedDevices ?: return emptyArray()
        return paired
            .filter {
                val n = it.name ?: return@filter false
                n.contains("PeriPage", ignoreCase = true) ||
                    n.contains("PT-", ignoreCase = true) ||
                    n.contains("PPG", ignoreCase = true)
            }
            .map { "${it.name}|${it.address}" }
            .toTypedArray()
    }

    // ---------- Connection ----------

    @SuppressLint("MissingPermission")
    fun connect(macAddress: String) {
        if (!connecting.compareAndSet(false, true)) {
            log("connect($macAddress) ignored: another attempt is still running")
            return
        }

        log("connect($macAddress) queued")
        executor.execute {
            try {
                closeQuietly()

                val adapter = BluetoothAdapter.getDefaultAdapter()
                    ?: throw IllegalStateException("no Bluetooth adapter")
                log("adapter: ${describeAdapter(adapter)}")
                if (!adapter.isEnabled) throw IllegalStateException("Bluetooth is off")

                // BLUETOOTH_CONNECT only exists on Android 12+ (API 31+).
                if (Build.VERSION.SDK_INT >= 31 &&
                    activity.checkSelfPermission("android.permission.BLUETOOTH_CONNECT")
                        != android.content.pm.PackageManager.PERMISSION_GRANTED
                ) {
                    throw SecurityException("BLUETOOTH_CONNECT not granted")
                }

                val device: BluetoothDevice = adapter.getRemoteDevice(macAddress)

                // Discovery slows down / breaks connection attempts.
                adapter.cancelDiscovery()
                log("device: bondState=${device.bondState} (12=bonded), name=${device.name}, ${describeDevice(device)}")

                val sock = openSocket(device)
                socket = sock
                outputStream = sock.outputStream

                log("connected")
                sendToUnity(CB_CONNECTED, macAddress)
            } catch (e: Exception) {
                Log.e(TAG, "Connect failed", e)
                closeQuietly()
                sendToUnity(CB_CONNECT_FAILED, "${e.javaClass.simpleName}: ${e.message}")
            } finally {
                connecting.set(false)
            }
        }
    }

    /**
     * Tries, in order: secure SPP (SDP lookup), insecure SPP, then direct
     * RFCOMM channels 1-3 via reflection (skips SDP, for printers whose SDP
     * record is missing or wrong). Each attempt logs how long it took.
     */
    @SuppressLint("MissingPermission")
    private fun openSocket(device: BluetoothDevice): BluetoothSocket {
        val attempts = mutableListOf<Pair<String, () -> BluetoothSocket>>(
            "secure SPP" to { device.createRfcommSocketToServiceRecord(SPP_UUID) },
            "insecure SPP" to { device.createInsecureRfcommSocketToServiceRecord(SPP_UUID) }
        )
        for (ch in 1..3) {
            attempts.add("reflection ch$ch" to {
                device.javaClass.getMethod("createRfcommSocket", Int::class.javaPrimitiveType)
                    .invoke(device, ch) as BluetoothSocket
            })
        }

        var last: Exception? = null
        for ((name, factory) in attempts) {
            var s: BluetoothSocket? = null
            val t0 = System.currentTimeMillis()
            try {
                log("trying $name")
                s = factory()
                s.connect()
                log("$name succeeded after ${System.currentTimeMillis() - t0}ms")
                return s
            } catch (e: Exception) {
                log("$name failed after ${System.currentTimeMillis() - t0}ms: ${e.javaClass.simpleName}: ${e.message}")
                try { s?.close() } catch (_: Exception) {}
                last = e
                Thread.sleep(300)
            }
        }
        throw last ?: IllegalStateException("all connect attempts failed")
    }

    fun disconnect() {
        executor.execute {
            closeQuietly()
            sendToUnity(CB_DISCONNECTED, "")
        }
    }

    private fun closeQuietly() {
        try { outputStream?.close() } catch (_: Exception) {}
        try { socket?.close() } catch (_: Exception) {}
        outputStream = null
        socket = null
    }

    // Note: BluetoothSocket.isConnected only says the socket connected at some
    // point, not that the printer is still reachable right now.
    fun isConnected(): Boolean {
        return socket?.isConnected == true
    }

    // ---------- Printing ----------

    /**
     * Writes an already-fully-framed print packet straight to the Bluetooth
     * socket. RFCOMM is a byte stream, so one write is fine.
     */
    fun printRaw(bytes: ByteArray) {
        log("printRaw(${bytes.size} bytes) queued")
        executor.execute {
            val os = outputStream
            if (os == null) {
                sendToUnity(CB_PRINT_FAILED, "not connected")
                return@execute
            }
            try {
                os.write(bytes)
                os.flush()
                log("printRaw written")
                sendToUnity(CB_PRINT_COMPLETE, "")
            } catch (e: Exception) {
                Log.e(TAG, "Print failed", e)
                sendToUnity(CB_PRINT_FAILED, "${e.javaClass.simpleName}: ${e.message}")
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

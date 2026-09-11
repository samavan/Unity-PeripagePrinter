//
//  PeripageMacPlugin.m
//
//  Native macOS plugin for Unity — Bluetooth Classic (SPP/RFCOMM) discovery
//  and connection via IOBluetooth. NOT CoreBluetooth — the Peripage P21
//  uses Bluetooth Classic, which CoreBluetooth (BLE-only) cannot see.
//
//  Exposes plain C functions Unity calls via [DllImport("PeripageMacPlugin")].
//  Unity polls state each frame/on-demand rather than using callbacks, to
//  keep the C#/Obj-C boundary simple.
//

#import <Foundation/Foundation.h>
#import <IOBluetooth/IOBluetooth.h>

#pragma mark - Discovered device storage

@interface PeripageDiscoveredDevice : NSObject
@property (nonatomic, copy) NSString *name;
@property (nonatomic, copy) NSString *address;
@end
@implementation PeripageDiscoveredDevice
@end

#pragma mark - Inquiry + connection delegate

@interface PeripageBTManager : NSObject <IOBluetoothDeviceInquiryDelegate, IOBluetoothRFCOMMChannelDelegate>
@property (nonatomic, strong) IOBluetoothDeviceInquiry *inquiry;
@property (nonatomic, strong) NSMutableArray<PeripageDiscoveredDevice *> *devices;
@property (nonatomic, strong) IOBluetoothDevice *connectedDevice;
@property (nonatomic, strong) IOBluetoothRFCOMMChannel *rfcommChannel;
@property (nonatomic, assign) BOOL isScanning;
@property (nonatomic, assign) BOOL isConnected;
@property (nonatomic, assign) BOOL lastConnectFailed;
@end

@implementation PeripageBTManager

+ (instancetype)shared {
    static PeripageBTManager *instance = nil;
    static dispatch_once_t onceToken;
    dispatch_once(&onceToken, ^{
        instance = [[PeripageBTManager alloc] init];
        instance.devices = [NSMutableArray array];
    });
    return instance;
}

- (void)startScan {
    [self.devices removeAllObjects];
    self.inquiry = [IOBluetoothDeviceInquiry inquiryWithDelegate:self];
    self.inquiry.inquiryLength = 10; // seconds
    self.inquiry.updateNewDeviceNames = YES;
    [self.inquiry start];
    self.isScanning = YES;
}

- (void)stopScan {
    [self.inquiry stop];
    self.isScanning = NO;
}

// --- IOBluetoothDeviceInquiryDelegate ---

- (void)deviceInquiryDeviceFound:(IOBluetoothDeviceInquiry *)sender device:(IOBluetoothDevice *)device {
    NSString *name = device.name ?: @"(unnamed)";
    NSString *address = device.addressString ?: @"";

    // Filter to likely Peripage devices so the list isn't cluttered with
    // every phone/headset/etc nearby. Remove this filter if you want to see
    // literally everything.
    if ([name rangeOfString:@"PPG" options:NSCaseInsensitiveSearch].location == NSNotFound &&
        [name rangeOfString:@"PeriPage" options:NSCaseInsensitiveSearch].location == NSNotFound) {
        return;
    }

    PeripageDiscoveredDevice *d = [[PeripageDiscoveredDevice alloc] init];
    d.name = name;
    d.address = address;
    [self.devices addObject:d];
}

- (void)deviceInquiryComplete:(IOBluetoothDeviceInquiry *)sender error:(IOReturn)error aborted:(BOOL)aborted {
    self.isScanning = NO;
}

// --- Connection ---

- (void)connectToAddress:(NSString *)address {
    self.lastConnectFailed = NO;
    IOBluetoothDevice *device = [IOBluetoothDevice deviceWithAddressString:address];
    if (!device) {
        self.lastConnectFailed = YES;
        return;
    }

    __weak typeof(self) weakSelf = self;
    IOReturn pairResult = [device openConnection];
    if (pairResult != kIOReturnSuccess) {
        self.lastConnectFailed = YES;
        return;
    }

    self.connectedDevice = device;

    // Standard SPP UUID, same one used on the Android side.
    IOBluetoothSDPUUID *sppUUID = [IOBluetoothSDPUUID uuid16:kBluetoothSDPUUID16ServiceClassSerialPort];
    IOBluetoothSDPServiceRecord *sppRecord = [device getServiceRecordForUUID:sppUUID];

    BluetoothRFCOMMChannelID channelID;
    if (sppRecord && [sppRecord getRFCOMMChannelID:&channelID] == kIOReturnSuccess) {
        IOBluetoothRFCOMMChannel *newChannel = nil;
        IOReturn openResult = [device openRFCOMMChannelAsync:&newChannel
                                                 withChannelID:channelID
                                                      delegate:self];
        self.rfcommChannel = newChannel;
        if (openResult != kIOReturnSuccess) {
            self.lastConnectFailed = YES;
        }
    } else {
        self.lastConnectFailed = YES;
    }

    (void)weakSelf;
}

- (void)disconnect {
    [self.rfcommChannel closeChannel];
    [self.connectedDevice closeConnection];
    self.isConnected = NO;
    self.rfcommChannel = nil;
    self.connectedDevice = nil;
}

- (BOOL)sendBytes:(const uint8_t *)bytes length:(NSUInteger)length {
    if (!self.rfcommChannel) return NO;
    // NOTE: mtu-chunking omitted for brevity — for larger images, split
    // into chunks no bigger than self.rfcommChannel.getMTU and call
    // writeSync repeatedly.
    IOReturn result = [self.rfcommChannel writeSync:(void *)bytes length:(UInt16)length];
    return result == kIOReturnSuccess;
}

// --- IOBluetoothRFCOMMChannelDelegate ---

- (void)rfcommChannelOpenComplete:(IOBluetoothRFCOMMChannel *)rfcommChannel status:(IOReturn)error {
    self.isConnected = (error == kIOReturnSuccess);
    self.lastConnectFailed = (error != kIOReturnSuccess);
}

- (void)rfcommChannelClosed:(IOBluetoothRFCOMMChannel *)rfcommChannel {
    self.isConnected = NO;
}

@end

#pragma mark - C bridge functions (called from Unity via DllImport)

extern "C" {

void Peripage_StartScan(void) {
    [[PeripageBTManager shared] startScan];
}

void Peripage_StopScan(void) {
    [[PeripageBTManager shared] stopScan];
}

int Peripage_IsScanning(void) {
    return [PeripageBTManager shared].isScanning ? 1 : 0;
}

int Peripage_GetDeviceCount(void) {
    return (int)[PeripageBTManager shared].devices.count;
}

// Caller must NOT free the returned pointer — it's owned by a static buffer
// that's overwritten on next call. Copy it immediately on the C# side.
const char *Peripage_GetDeviceName(int index) {
    NSArray *devices = [PeripageBTManager shared].devices;
    if (index < 0 || index >= (int)devices.count) return "";
    static char buffer[256];
    strncpy(buffer, [((PeripageDiscoveredDevice *)devices[index]).name UTF8String], sizeof(buffer) - 1);
    return buffer;
}

const char *Peripage_GetDeviceAddress(int index) {
    NSArray *devices = [PeripageBTManager shared].devices;
    if (index < 0 || index >= (int)devices.count) return "";
    static char buffer[64];
    strncpy(buffer, [((PeripageDiscoveredDevice *)devices[index]).address UTF8String], sizeof(buffer) - 1);
    return buffer;
}

void Peripage_Connect(const char *address) {
    NSString *addr = [NSString stringWithUTF8String:address];
    [[PeripageBTManager shared] connectToAddress:addr];
}

void Peripage_Disconnect(void) {
    [[PeripageBTManager shared] disconnect];
}

int Peripage_IsConnected(void) {
    return [PeripageBTManager shared].isConnected ? 1 : 0;
}

int Peripage_LastConnectFailed(void) {
    return [PeripageBTManager shared].lastConnectFailed ? 1 : 0;
}

int Peripage_SendBytes(const unsigned char *bytes, int length) {
    return [[PeripageBTManager shared] sendBytes:bytes length:(NSUInteger)length] ? 1 : 0;
}

} // extern "C"

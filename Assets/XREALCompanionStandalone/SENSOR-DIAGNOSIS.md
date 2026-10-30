UPDATE: The native SDK failure described below has been bypassed by Direct USB v1. The actual Unity executable now reads live Air 2 samples and calibrates successfully. See README.md and Builds/direct-usb-diagnostic.log.

# Air 2 tracking diagnosis — 2026-10-29

## Verified on the connected device

The standalone player's sensor-only mode was executed against the connected Air 2 with no VertoXR/Nebula/XREAL competitor running. It exited with code 2 (native session creation failed). The mode does not change display modes.

Log: `Builds/sensor-diagnostic.log`.

Relevant native output:

```
CreateSession: XREAL_DEVICE_TYPE_AIR2
MCU device type [0]
sensor_type:0 not support device type[0]
Generic Control open failed, win_error=0, errno = 2
ControlChannelInitFail, msg=NativeGlasses+Start
Glasses Start Failed!
sessionOpened=False
```

Windows reports the VID_3318/PID_0428 Air 2 composite device and its HID interfaces MI_03/MI_04/MI_05 as healthy. This does not prove the vendor runtime can open them. The root cause inside the native driver (device matching, firmware compatibility, or control-interface access) remains unresolved; no evidence justifies blaming the GPU.

## App corrections

- Removed the invented 180-degree canvas roll correction.
- Added an explicit Input System TrackedPoseDriver to CenterCamera, with XRHMD centerEyeRotation and trackingState bindings.
- Disabled the sample manager's automatic unchecked session creation. The app now checks CreateSession's return value before starting XR.
- Require isTracked and the Rotation tracking-state bit before claiming a head pose is valid.
- Preserve the independent world anchor and native TryRecenter flow.

Windows compilation/build passed. Physical panel orientation and anchoring are not verified because the native sensor session fails first.

## Remaining work

Repair or replace the native Air 2 sensor backend. Repeating recenter calls cannot produce orientation data from a failed session. An updated compatible XREAL PC runtime or a direct HID driver with sensor fusion is required to resolve that layer.

Sources consulted:

- https://github.com/dengxian-xreal/xreal.github.io/blob/main/docs/14_PC%20SDK%20%28Beta%29.md
- https://docs.unity.cn/Packages/com.unity.inputsystem%401.7/api/UnityEngine.InputSystem.XR.TrackedPoseDriver.html
- https://github.com/badicsalex/ar-drivers-rs


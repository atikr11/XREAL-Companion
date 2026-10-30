// Windows Air 2 sensor-access probe. No XREAL runtime dependency.
// Protocol reference: https://github.com/badicsalex/ar-drivers-rs (MIT),
// src/nreal_air.rs by Alex Badics and contributors.
using System;
using System.IO;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

public sealed class DirectAir2Hid : IDisposable
{
    [StructLayout(LayoutKind.Sequential)]
    struct InterfaceData { public int Size; public Guid Guid; public int Flags; public IntPtr Reserved; }
    [DllImport("hid.dll")] static extern void HidD_GetHidGuid(out Guid guid);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid guid, string enumerator, IntPtr parent, uint flags);
    [DllImport("setupapi.dll", SetLastError=true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr info, ref Guid guid, uint index, ref InterfaceData data);
    [DllImport("setupapi.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref InterfaceData data, IntPtr detail, uint size, out uint required, IntPtr info);
    [DllImport("setupapi.dll")] static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    static List<string> Paths()
    {
        HidD_GetHidGuid(out Guid guid);
        IntPtr set=SetupDiGetClassDevs(ref guid,null,IntPtr.Zero,0x12);
        if(set==new IntPtr(-1)) throw new System.ComponentModel.Win32Exception();
        var result=new List<string>();
        try {
            for(uint i=0;;i++) {
                var data=new InterfaceData { Size=Marshal.SizeOf(typeof(InterfaceData)) };
                if(!SetupDiEnumDeviceInterfaces(set,IntPtr.Zero,ref guid,i,ref data)) break;
                SetupDiGetDeviceInterfaceDetail(set,ref data,IntPtr.Zero,0,out uint size,IntPtr.Zero);
                IntPtr detail=Marshal.AllocHGlobal((int)size);
                try {
                    Marshal.WriteInt32(detail,IntPtr.Size==8?8:6);
                    if(!SetupDiGetDeviceInterfaceDetail(set,ref data,detail,size,out size,IntPtr.Zero)) continue;
                    string path=Marshal.PtrToStringUni(IntPtr.Add(detail,4));
                    if(path.IndexOf("vid_3318&pid_0428",StringComparison.OrdinalIgnoreCase)>=0) result.Add(path);
                } finally { Marshal.FreeHGlobal(detail); }
            }
        } finally { SetupDiDestroyDeviceInfoList(set); }
        return result;
    }
    static SafeFileHandle Open(string path)
    {
        var handle=CreateFile(path,0xC0000000,3,IntPtr.Zero,3,0x40000000,IntPtr.Zero);
        if(handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return handle;
    }
    static byte[] StreamCommand(bool on)
    {
        var p=new byte[65]; p[1]=0xaa; p[6]=4; p[8]=0x19; p[9]=(byte)(on?1:0);
        uint crc=0xffffffff;
        for(int i=6;i<10;i++) { crc^=p[i]; for(int b=0;b<8;b++) crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0); }
        Array.Copy(BitConverter.GetBytes(~crc),0,p,2,4);
        return p;
    }
    static void Send(SafeFileHandle stream, bool on)
    {
        using(var cancel=new CancellationTokenSource(1000)) {
            byte[] p=StreamCommand(on);
            Transfer(stream,p,true,cancel.Token);
        }
    }
    static int Signed24(byte[] p,int i) { int v=p[i]|(p[i+1]<<8)|(p[i+2]<<16); return (v&0x800000)!=0?v|unchecked((int)0xff000000):v; }
    [StructLayout(LayoutKind.Sequential)]
    struct Overlapped { public IntPtr Internal,InternalHigh; public uint Offset,OffsetHigh; public IntPtr Event; }
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool ReadFile(SafeFileHandle h,IntPtr buffer,uint size,out uint read,IntPtr ov);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool WriteFile(SafeFileHandle h,IntPtr buffer,uint size,out uint written,IntPtr ov);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetOverlappedResult(SafeFileHandle h,IntPtr ov,out uint count,bool wait);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool CancelIoEx(SafeFileHandle h,IntPtr ov);
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr CreateEvent(IntPtr attributes,bool manual,bool initial,string name);
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr h,uint milliseconds);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
    static int Transfer(SafeFileHandle h,byte[] bytes,bool write,CancellationToken stop)
    {
        IntPtr evt=CreateEvent(IntPtr.Zero,true,false,null);
        if(evt==IntPtr.Zero) throw new System.ComponentModel.Win32Exception();
        IntPtr ov=Marshal.AllocHGlobal(Marshal.SizeOf(typeof(Overlapped)));
        var pinned=GCHandle.Alloc(bytes,GCHandleType.Pinned);
        bool pending=false;
        try {
            Marshal.StructureToPtr(new Overlapped { Event=evt },ov,false);
            uint count;
            bool ok=write?WriteFile(h,pinned.AddrOfPinnedObject(),(uint)bytes.Length,out count,ov):ReadFile(h,pinned.AddrOfPinnedObject(),(uint)bytes.Length,out count,ov);
            if(!ok && Marshal.GetLastWin32Error()!=997) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            pending=true;
            while(WaitForSingleObject(evt,25)==258) stop.ThrowIfCancellationRequested();
            if(!GetOverlappedResult(h,ov,out count,false)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            pending=false;
            return (int)count;
        } finally {
            if(pending) { CancelIoEx(h,ov); GetOverlappedResult(h,ov,out uint ignored,true); }
            pinned.Free(); Marshal.FreeHGlobal(ov); CloseHandle(evt);
        }
    }
    public struct Sample { public ulong Timestamp; public float Gx,Gy,Gz,Ax,Ay,Az; }
    private SafeFileHandle imu, mcu;
    private byte originalMode;
    private bool modeChanged;
    public DirectAir2Hid(bool stereo)
    {
        try {
            var paths=Paths();
            string imuPath=paths.Find(p=>p.Contains("mi_03"));
            if(imuPath==null) throw new IOException("Air 2 motion interface not found. Connect the glasses.");
            imu=Open(imuPath);
            if(stereo) {
                string mcuPath=paths.Find(p=>p.Contains("mi_04"));
                if(mcuPath==null) throw new IOException("Air 2 display-control interface not found.");
                mcu=Open(mcuPath);
                byte[] mode=Control(7,null);
                if(mode.Length<2 || mode[0]!=0) throw new IOException("Cannot read Air 2 display mode.");
                originalMode=mode[1];
                if(originalMode!=3) { modeChanged=true; Control(8,new byte[]{3}); }
            }
            Send(imu,true);
        } catch { Dispose(); throw; }
    }
    byte[] Control(ushort command,byte[] data)
    {
        data=data??new byte[0];
        byte[] p=new byte[65]; p[1]=0xfd;
        ushort length=(ushort)(17+data.Length);
        Array.Copy(BitConverter.GetBytes(length),0,p,6,2);
        Array.Copy(BitConverter.GetBytes(0x1337u),0,p,8,4);
        Array.Copy(BitConverter.GetBytes(command),0,p,16,2);
        Array.Copy(data,0,p,23,data.Length);
        uint crc=0xffffffff;
        for(int i=6;i<6+length;i++) { crc^=p[i]; for(int b=0;b<8;b++) crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0); }
        Array.Copy(BitConverter.GetBytes(~crc),0,p,2,4);
        using(var cancel=new CancellationTokenSource(1000)) Transfer(mcu,p,true,cancel.Token);
        for(int i=0;i<32;i++) {
            p=new byte[65]; int n;
            using(var cancel=new CancellationTokenSource(1000)) n=Transfer(mcu,p,false,cancel.Token);
            int offset=p[0]==0?1:0;
            if(n-offset<23 || p[offset]!=0xfd || BitConverter.ToUInt16(p,offset+15)!=command) continue;
            int count=BitConverter.ToUInt16(p,offset+5)-17;
            if(count<0 || count>42 || offset+22+count>n) throw new IOException("Invalid Air 2 control response.");
            var result=new byte[count]; Array.Copy(p,offset+22,result,0,count);
            if(command==8 && (count==0 || result[0]!=0)) throw new IOException("Air 2 rejected display mode.");
            return result;
        }
        throw new IOException("No Air 2 control acknowledgement.");
    }
    public bool Read(CancellationToken stop,out Sample sample)
    {
        sample=new Sample(); var p=new byte[65]; int n;
        using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop)) {
            timeout.CancelAfter(1000);
            n=Transfer(imu,p,false,timeout.Token);
        }
        int o=p[0]==0?1:0;
        if(n-o<42 || p[o]!=1 || p[o+1]!=2) return false;
        if(o!=0) Array.Copy(p,o,p,0,n-o);
        float gd=BitConverter.ToUInt32(p,14), ad=BitConverter.ToUInt32(p,29);
        if(gd==0 || ad==0) return false;
        float gs=BitConverter.ToUInt16(p,12)/gd, a=BitConverter.ToUInt16(p,27)/ad;
        // RUB right-handed sensor frame -> Unity RUF. Angular velocity is axial.
        sample=new Sample { Timestamp=BitConverter.ToUInt64(p,4),
            Gx=Signed24(p,18)*gs, Gy=-Signed24(p,24)*gs, Gz=Signed24(p,21)*gs,
            Ax=-Signed24(p,33)*a, Ay=Signed24(p,39)*a, Az=-Signed24(p,36)*a };
        return true;
    }
    public void Dispose()
    {
        if(imu!=null) { try { Send(imu,false); } catch {} imu.Dispose(); imu=null; }
        if(mcu!=null) { if(modeChanged) { try { Control(8,new byte[]{originalMode}); } catch {} } mcu.Dispose(); mcu=null; }
    }
}



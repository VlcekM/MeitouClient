using System.Runtime.InteropServices;
using SharpMetal.ObjectiveCCore;

namespace Meitou.Game.Rendering.Metal;

/// <summary>
/// Counts the ended native full-screen transitions of the game's window. AppKit posts <c>NSWindowDidEnterFullScreenNotification</c> and
/// <c>NSWindowDidExitFullScreenNotification</c> once the animation into or out of the Space is over (a failed transition posts neither). The observer
/// is an object of an Objective-C class registered at run time, its one method a managed function; the notifications arrive on the main thread
/// while the window's events are handled. A run has one game window, so the count is the process's.
/// </summary>
static unsafe class FullScreenTransitions
{
    /// <summary>
    /// The path of the Objective-C runtime.
    /// </summary>
    const string ObjC = "/usr/lib/libobjc.A.dylib";

    /// <summary>
    /// The notifications the observer counts.
    /// </summary>
    static readonly string[] Notifications = ["NSWindowDidEnterFullScreenNotification", "NSWindowDidExitFullScreenNotification"];

    /// <summary>
    /// The observer's method.
    /// </summary>
    static readonly Selector Ended = new("meitouFullScreenTransitionEnded:");

    /// <summary>
    /// The <c>addObserver:selector:name:object:</c> selector.
    /// </summary>
    static readonly Selector AddObserver = new("addObserver:selector:name:object:");

    /// <summary>
    /// The transitions ended so far.
    /// </summary>
    static int s_count;

    /// <summary>
    /// The transitions ended so far (a request's transition is over once this has moved past its value at the request).
    /// </summary>
    public static int Count => Volatile.Read(ref s_count);

    /// <summary>
    /// Counts the transitions of <paramref name="nsWindow"/> from now on. The observer is never released: it lives as long as the window.
    /// </summary>
    public static void Observe(IntPtr nsWindow)
    {
        var observer = new ObjectiveCClass(ObserverClass()).AllocInit();
        var center = ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("NSNotificationCenter"), "defaultCenter");
        foreach (string name in Notifications)
        {
            var nsName = ObjectiveC.IntPtr_objc_msgSend(ObjectiveC.objc_getClass("NSString"), "stringWithUTF8String:", name);
            SendObserver(center, AddObserver.SelPtr, observer, Ended.SelPtr, nsName, nsWindow);
        }
    }

    /// <summary>
    /// The observer's class: an NSObject whose <see cref="Ended"/> method counts (registered on first use).
    /// </summary>
    static IntPtr ObserverClass()
    {
        var existing = ObjectiveC.objc_getClass("MeitouFullScreenObserver");
        if (existing != IntPtr.Zero) return existing;

        var observerClass = AllocateClass(ObjectiveC.objc_getClass("NSObject"), "MeitouFullScreenObserver\0"u8);
        var method = (IntPtr)(delegate* unmanaged<IntPtr, IntPtr, IntPtr, void>)&TransitionEnded;
        AddMethod(observerClass, Ended.SelPtr, method, "v@:@\0"u8);   // returns void; self, _cmd and the notification
        RegisterClassPair(observerClass);
        return observerClass;
    }

    /// <summary>
    /// The observer's method: a transition ended.
    /// </summary>
    [UnmanagedCallersOnly]
    static void TransitionEnded(IntPtr self, IntPtr selector, IntPtr notification) => Interlocked.Increment(ref s_count);

    /// <summary>
    /// A new subclass of <paramref name="superclass"/> named <paramref name="name"/> (NUL-terminated UTF-8), to be registered once its methods
    /// are added.
    /// </summary>
    static IntPtr AllocateClass(IntPtr superclass, ReadOnlySpan<byte> name)
    {
        fixed (byte* n = name) return AllocateClassPair(superclass, n, 0);
    }

    /// <summary>
    /// Adds a method to an unregistered class; <paramref name="types"/> is its NUL-terminated UTF-8 type encoding.
    /// </summary>
    static void AddMethod(IntPtr cls, IntPtr selector, IntPtr implementation, ReadOnlySpan<byte> types)
    {
        fixed (byte* t = types) ClassAddMethod(cls, selector, implementation, t);
    }

    /// <summary>
    /// The runtime's <c>objc_allocateClassPair</c>.
    /// </summary>
    [DllImport(ObjC, EntryPoint = "objc_allocateClassPair")]
    static extern IntPtr AllocateClassPair(IntPtr superclass, byte* name, nuint extraBytes);

    /// <summary>
    /// The runtime's <c>class_addMethod</c>.
    /// </summary>
    [DllImport(ObjC, EntryPoint = "class_addMethod")]
    static extern byte ClassAddMethod(IntPtr cls, IntPtr selector, IntPtr implementation, byte* types);

    /// <summary>
    /// The runtime's <c>objc_registerClassPair</c>.
    /// </summary>
    [DllImport(ObjC, EntryPoint = "objc_registerClassPair")]
    static extern void RegisterClassPair(IntPtr cls);

    /// <summary>
    /// <c>objc_msgSend</c> with four pointer arguments (for <c>addObserver:selector:name:object:</c>).
    /// </summary>
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    static extern void SendObserver(IntPtr receiver, IntPtr selector, IntPtr observer, IntPtr observerSelector, IntPtr name, IntPtr sender);
}

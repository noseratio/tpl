/// Provides a pool of single-threaded apartments.
/// Each apartment provides asynchronous continuation after `await` on the same thread, 
/// via ThreadWithAffinityContext object
/// Related: http://stackoverflow.com/q/20993007/1768303, http://stackoverflow.com/q/21211998/1768303
/// Partially based on StaTaskScheduler from http://blogs.msdn.com/b/pfxteam/archive/2010/04/07/9990421.aspx
/// MIT License, (c) 2014 by Noseratio - http://stackoverflow.com/users/1768303/noseratio
/// 
/// TODO: add unit tests

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Noseratio.ThreadAffinity
{
	/// <summary>A simple callback interface to execute an item (used with BlockingCollection).</summary>
	internal interface IExecuteItem
	{
		void Execute();
	}

	/// <summary>Customize a blocking wait (e.g., with a message pump)</summary>
	public delegate int WaitHelperFunc(IntPtr[] waitHandles, bool waitAll, int millisecondsTimeout);

	/// <summary>Customize a blocking retrieval of a queued task item</summary>
	internal delegate IExecuteItem TakeFunc(BlockingCollection<IExecuteItem> items, CancellationToken token);

	#region ThreadAffinityTaskScheduler
	/// <summary>
	/// Provides a pool of single-threaded apartments, with optional message pumping
	/// Each apartment provides asynchronous continuation after `await` on the same thread, 
	/// </summary>
	public sealed class ThreadAffinityTaskScheduler : TaskScheduler, IDisposable
	{
		/// <summary>Stores the queued tasks to be executed by one of our apartments.</summary>
		private BlockingCollection<IExecuteItem> _items;
		/// <summary>The Cancellation Token Source object to request the termination.</summary>
		private readonly CancellationTokenSource _terminationCts;
		/// <summary>The list of SingleThreadSynchronizationContext objects to represent apartments (threads).</summary>
		private readonly List<ThreadWithAffinityContext> _threads;

		/// <summary>Initializes a new instance of the ThreadAffinityTaskScheduler class with the specified concurrency level.</summary>
		/// <param name="numberOfThreads">The number of threads that should be created and used by this scheduler.</param>
		public ThreadAffinityTaskScheduler(int numberOfThreads, bool staThreads = false, WaitHelperFunc waitHelper = null)
		{
			// Validate arguments
			if (numberOfThreads < 1)
				throw new ArgumentOutOfRangeException("numberOfThreads");

			// Initialize the tasks collection
			_items = new BlockingCollection<IExecuteItem>();

			// CTS to cancel task dispatching 
			_terminationCts = new CancellationTokenSource();

			// Create the contexts (threads) to be used by this scheduler
			_threads = Enumerable.Range(0, numberOfThreads).Select(i =>
				new ThreadWithAffinityContext(_terminationCts.Token, staThreads, waitHelper, Take)).ToList();
		}

		/// <summary>
		/// Take a queued task item either from ThreadAffinityTaskScheduler or ThreadWithAffinityContext, 
		/// blocks if both queues are empty
		/// </summary>
		internal IExecuteItem Take(BlockingCollection<IExecuteItem> items, CancellationToken token)
		{
			IExecuteItem item;
			BlockingCollection<IExecuteItem>.TakeFromAny(new[] {_items, items }, out item, token);
			return item;
		}

		/// <summary>Queues a Task to be executed by this scheduler.</summary>
		/// <param name="task">The task to be executed.</param>
		protected override void QueueTask(Task task)
		{
			// Push the task into the blocking collection of tasks
			_items.Add(new TaskItem(task, this.TryExecuteTask));
		}

		/// <summary>Provides a list of the scheduled tasks for the debugger to consume.</summary>
		/// <returns>An enumerable of all tasks currently scheduled.</returns>
		protected override IEnumerable<Task> GetScheduledTasks()
		{
			// Serialize the contents of the blocking collection of tasks for the debugger
			return _items.Select(t => ((TaskItem)t).Task).ToArray();
		}

		/// <summary>Determines whether a Task may be inlined.</summary>
		/// <param name="task">The task to be executed.</param>
		/// <param name="taskWasPreviouslyQueued">Whether the task was previously queued.</param>
		/// <returns>true if the task was successfully inlined; otherwise, false.</returns>
		protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued)
		{
			//TODO: call TryExecuteTask(task) if can be done on the same thread
			// not sure if that's possible to implement as we don't have an antecedent task to match the thread
			return false;
		}

		/// <summary>Gets the maximum concurrency level supported by this scheduler.</summary>
		public override int MaximumConcurrencyLevel
		{
			get { return _threads.Count; }
		}

		/// <summary>
		/// Request the termination and so the cleanup.
		/// This method blocks until all threads successfully shutdown.
		/// </summary>
		public void Dispose()
		{
			if (_items != null)
			{
				// Request the cancellation
				_terminationCts.Cancel();

				// Dispose each context
				foreach (var context in _threads)
					context.Dispose();

				// Cleanup
				_items.Dispose();
				_items = null;
			}
		}

		/// <summary>A handy wrapper around Task.Factory.StartNew</summary>
		public Task Run(Func<Task> action, CancellationToken token)
		{
			return Task.Factory.StartNew(action, token, TaskCreationOptions.None, this).Unwrap();
		}

		/// <summary>A handy wrapper around Task.Factory.StartNew</summary>
		public Task<TResult> Run<TResult>(Func<Task<TResult>> action, CancellationToken token)
		{
			return Task.Factory.StartNew(action, token, TaskCreationOptions.None, this).Unwrap();
		}

		#region TaskItem
		/// <summary>
		/// A simple container to store and execute a task via IExecuteItem.Execute
		/// </summary>
		internal class TaskItem : IExecuteItem
		{
			public Task Task { get; private set; }
			readonly Func<Task, bool> _executeTask;

			protected TaskItem() { }

			public TaskItem(Task task, Func<Task, bool> executeTask)
			{
				this.Task = task;
				_executeTask = executeTask;
			}

			// IExecuteItem.Execute
			public void Execute()
			{
				_executeTask(this.Task);
			}
		}
		#endregion
	}
	#endregion

	#region ThreadWithAffinityContext
	/// <summary>
	/// A helper Synchronization Context class to post continuations on the same thread
	/// (was: SingleThreadSynchronizationContext)
	/// </summary>
	public class ThreadWithAffinityContext : SynchronizationContext, IDisposable
	{
		private BlockingCollection<IExecuteItem> _items; // pending tasks
		WaitHelperFunc _waitHelper; // allows to customize the blocking wait (with message pump)
		TakeFunc _takeFunc; // pump the task queue
		Thread _thread; // the thread with the sync context installed on it 
		CancellationTokenSource _cts; // ask the thread to end

		/// <summary>// TaskScheduler.FromCurrentSynchronizationContext()</summary>
		public TaskScheduler Scheduler { get; private set; } 

		/// <summary>
		/// Construct a standaline instance of ThreadWithAffinityContext
		/// </summary>
		public ThreadWithAffinityContext(bool staThread = true, bool pumpMessages = true):
			this(CancellationToken.None,
				staThread, 
				pumpMessages ? WaitHelpers.WaitWithMessageLoop : (WaitHelperFunc)null,
				ThreadWithAffinityContext.Take)
		{
		}

		/// <summary>
		/// Construct a standaline instance of ThreadWithAffinityContext with cancellation
		/// </summary>
		public ThreadWithAffinityContext(CancellationToken token, bool staThread = true, bool pumpMessages = true) :
			this(token,
				staThread,
				pumpMessages ? WaitHelpers.WaitWithMessageLoop : (WaitHelperFunc)null,
				ThreadWithAffinityContext.Take)
		{
		}

		/// <summary>
		/// Construct an instance of ThreadWithAffinityContext to used by ThreadAffinityTaskScheduler
		/// </summary>
		internal ThreadWithAffinityContext(CancellationToken token, bool staThread, WaitHelperFunc waitHelper, TakeFunc takeFunc)
		{
			_waitHelper = waitHelper;
			_items = new BlockingCollection<IExecuteItem>();
			_takeFunc = takeFunc;
			_cts = CancellationTokenSource.CreateLinkedTokenSource(token);

			if (_takeFunc == null)
				_takeFunc = Take;

			// this makes our override of SynchronizationContext.Wait get called
			if (_waitHelper == null)
				_waitHelper = WaitHelper;
			else
				base.SetWaitNotificationRequired();

			// use TCS to return SingleThreadSynchronizationContext to the task scheduler
			var tcs = new TaskCompletionSource<TaskScheduler>();

			_thread = new Thread(() =>
			{
				// install on the current thread
				SynchronizationContext.SetSynchronizationContext(this);
				try
				{
					tcs.SetResult(TaskScheduler.FromCurrentSynchronizationContext()); // the sync. context is ready, return it to the task scheduler

					// the thread's core task dispatching loop, terminate-able with token
					while (true)
					{
						var executeItem = _takeFunc(_items, _cts.Token);
						// execute a Task queued by ThreadAffinityTaskScheduler.QueueTask
						// or a callback queued by SingleThreadSynchronizationContext.Post
						executeItem.Execute();
					};
				}
				catch (OperationCanceledException)
				{
					// ignore OperationCanceledException exceptions when terminating
					if (!_cts.Token.IsCancellationRequested)
						throw;
				}
				finally
				{
					SynchronizationContext.SetSynchronizationContext(null);
				}
			});

			// make it an STA thread if message pumping is requested
			if (staThread)
				_thread.SetApartmentState(ApartmentState.STA);

			_thread.IsBackground = true;
			_thread.Start();
			this.Scheduler = tcs.Task.Result;
		}

		public override void Post(SendOrPostCallback d, object state)
		{
			_items.Add(new ActionItem(() => d(state)));
		}

		public override void Send(SendOrPostCallback d, object state)
		{
			//TODO: currently we don't support (and expect) synchronous callbacks
			throw new NotImplementedException();
		}

		public override SynchronizationContext CreateCopy()
		{
			// more info: http://stackoverflow.com/q/21062440
			return this;
		}

		public override int Wait(IntPtr[] waitHandles, bool waitAll, int millisecondsTimeout)
		{
			// this can pump if needed
			return _waitHelper(waitHandles, waitAll, millisecondsTimeout);
		}

		public void Dispose()
		{
			// cleanup
			if (_thread != null)
			{
				// request the cancellation and wait
				_cts.Cancel();
				_thread.Join();
				_thread = null;
			}

			if (_items != null)
			{
				_items.Dispose();
				_items = null;
			}
		}

		/// <summary>A handy wrapper around Task.Factory.StartNew</summary>
		public Task Run(Func<Task> action, CancellationToken token)
		{
			return Task.Factory.StartNew(action, token, TaskCreationOptions.None, this.Scheduler).Unwrap();
		}

		/// <summary>A handy wrapper around Task.Factory.StartNew</summary>
		public Task<TResult> Run<TResult>(Func<Task<TResult>> action, CancellationToken token)
		{
			return Task.Factory.StartNew(action, token, TaskCreationOptions.None, this.Scheduler).Unwrap();
		}

		/// <summary>
		/// Take queued task item, blocks if the queue is empty
		/// </summary>
		static internal IExecuteItem Take(BlockingCollection<IExecuteItem> items, CancellationToken token)
		{
			return items.Take(token);
		}

		/// <summary>
		/// A simple container to store and execute an action via IExecuteItem.Execute
		/// </summary>
		class ActionItem : IExecuteItem
		{
			readonly Action _action;

			protected ActionItem() { }

			public ActionItem(Action action)
			{
				_action = action;
			}

			// IExecuteItem.Execute
			public void Execute()
			{
				_action();
			}
		}
	}
	#endregion

	#region WaitHelpers
	/// <summary>
	/// Wait for handle(s) with message loop and timeout 
	/// </summary>
	public static class WaitHelpers
	{
		/// <summary>
		/// Wait for handles similar to SynchronizationContext.WaitHelper, but with pumping 
		/// </summary>
		public static int WaitWithMessageLoop(IntPtr[] waitHandles, bool waitAll, int timeout)
		{
			// Don't use CoWaitForMultipleHandles, it has issues with message pumping
			// more info: http://stackoverflow.com/q/21226600/1768303 

			const uint QS_MASK = NativeMethods.QS_ALLINPUT; // message queue status

			uint count = (uint)waitHandles.Length;

			if (waitHandles == null || count == 0)
				throw new ArgumentNullException();

			uint nativeResult; // result of the native wait API (WaitForMultipleObjects or MsgWaitForMultipleObjectsEx)
			int managedResult; // result to return from WaitHelper

			// wait for all?
			if (waitAll && count > 1)
			{
				// more: http://blogs.msdn.com/b/cbrumme/archive/2004/02/02/66219.aspx, search for "mutex"
				throw new NotSupportedException("WaitAll for multiple handles on a STA thread is not supported.");
			}
			else
			{
				// optimization: a quick check with a zero timeout
				nativeResult = NativeMethods.WaitForMultipleObjects(count, waitHandles, bWaitAll: false, dwMilliseconds: 0);
				if (IsNativeWaitSuccessful(count, nativeResult, out managedResult))
					return managedResult;

				// proceed to pumping

				// track timeout if not infinite
				Func<bool> hasTimedOut = () => false;
				int remainingTimeout = timeout;

				if (remainingTimeout != Timeout.Infinite)
				{
					int startTick = Environment.TickCount;
					hasTimedOut = () =>
					{
						// Environment.TickCount wraps correctly even if runs continuously 
						int lapse = Environment.TickCount - startTick;
						remainingTimeout = Math.Max(timeout - lapse, 0);
						return remainingTimeout <= 0;
					};
				}

				// the core loop
				var msg = new NativeMethods.MSG();
				while (true)
				{
					// MsgWaitForMultipleObjectsEx with MWMO_INPUTAVAILABLE returns,
					// even if there's a message already seen but not removed in the message queue
					nativeResult = NativeMethods.MsgWaitForMultipleObjectsEx(
						count, waitHandles,
						(uint)remainingTimeout,
						QS_MASK,
						NativeMethods.MWMO_INPUTAVAILABLE);

					if (IsNativeWaitSuccessful(count, nativeResult, out managedResult) || WaitHandle.WaitTimeout == managedResult)
						return managedResult;

					// there is a message, pump and dispatch it
					if (NativeMethods.PeekMessage(out msg, IntPtr.Zero, 0, 0, NativeMethods.PM_REMOVE))
					{
						NativeMethods.TranslateMessage(ref msg);
						NativeMethods.DispatchMessage(ref msg);
					}
					if (hasTimedOut())
						return WaitHandle.WaitTimeout;
				}
			}
		}

		/// <summary>
		/// Analyze the result of the native wait API
		/// </summary>
		static bool IsNativeWaitSuccessful(uint count, uint nativeResult, out int managedResult)
		{
			if (nativeResult == (NativeMethods.WAIT_OBJECT_0 + count))
			{
				// only valid for MsgWaitForMultipleObjectsEx
				managedResult = int.MaxValue;
				return false;
			}

			if (nativeResult >= NativeMethods.WAIT_OBJECT_0 && nativeResult < (NativeMethods.WAIT_OBJECT_0 + count))
			{
				managedResult = unchecked((int)(nativeResult - NativeMethods.WAIT_OBJECT_0));
				return true;
			}

			if (nativeResult >= NativeMethods.WAIT_ABANDONED_0 && nativeResult < (NativeMethods.WAIT_ABANDONED_0 + count))
			{
				managedResult = unchecked((int)(nativeResult - NativeMethods.WAIT_ABANDONED_0));
				return true;
			}

			if (nativeResult == NativeMethods.WAIT_TIMEOUT)
			{
				managedResult = WaitHandle.WaitTimeout;
				return false;
			}

			throw new InvalidOperationException();
		}

		#region NativeMethods
		/// <summary>
		/// NativeMethods - p/invoke declarations
		/// </summary>
		internal static class NativeMethods
		{
			public const uint QS_KEY = 0x0001;
			public const uint QS_MOUSEMOVE = 0x0002;
			public const uint QS_MOUSEBUTTON = 0x0004;
			public const uint QS_POSTMESSAGE = 0x0008;
			public const uint QS_TIMER = 0x0010;
			public const uint QS_PAINT = 0x0020;
			public const uint QS_SENDMESSAGE = 0x0040;
			public const uint QS_HOTKEY = 0x0080;
			public const uint QS_ALLPOSTMESSAGE = 0x0100;
			public const uint QS_RAWINPUT = 0x0400;

			public const uint QS_MOUSE = (QS_MOUSEMOVE | QS_MOUSEBUTTON);
			public const uint QS_INPUT = (QS_MOUSE | QS_KEY | QS_RAWINPUT);
			public const uint QS_ALLEVENTS = (QS_INPUT | QS_POSTMESSAGE | QS_TIMER | QS_PAINT | QS_HOTKEY);
			public const uint QS_ALLINPUT = (QS_INPUT | QS_POSTMESSAGE | QS_TIMER | QS_PAINT | QS_HOTKEY | QS_SENDMESSAGE);

			public const uint MWMO_INPUTAVAILABLE = 0x0004;
			public const uint MWMO_WAITALL = 0x0001;

			public const uint PM_REMOVE = 0x0001;
			public const uint PM_NOREMOVE = 0;

			public const uint WAIT_TIMEOUT = 0x00000102;
			public const uint WAIT_FAILED = 0xFFFFFFFF;
			public const uint INFINITE = 0xFFFFFFFF;
			public const uint WAIT_OBJECT_0 = 0;
			public const uint WAIT_ABANDONED_0 = 0x00000080;
			public const uint WAIT_IO_COMPLETION = 0x000000C0;

			[StructLayout(LayoutKind.Sequential)]
			public struct MSG
			{
				public IntPtr hwnd;
				public uint message;
				public IntPtr wParam;
				public IntPtr lParam;
				public uint time;
				public int x;
				public int y;
			}

			[DllImport("user32.dll")]
			[return: MarshalAs(UnmanagedType.Bool)]
			public static extern bool PeekMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

			[DllImport("user32.dll")]
			[return: MarshalAs(UnmanagedType.Bool)]
			public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

			[DllImport("user32.dll")]
			[return: MarshalAs(UnmanagedType.Bool)]
			public static extern bool SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

			[DllImport("user32.dll")]
			public static extern IntPtr DispatchMessage([In] ref MSG lpMsg);

			[DllImport("user32.dll")]
			[return: MarshalAs(UnmanagedType.Bool)]
			public static extern bool TranslateMessage([In] ref MSG lpMsg);

			[DllImport("ole32.dll", PreserveSig = false)]
			public static extern void OleInitialize(IntPtr pvReserved);

			[DllImport("ole32.dll", PreserveSig = true)]
			public static extern void OleUninitialize();

			[DllImport("kernel32.dll")]
			public static extern uint GetTickCount();

			[DllImport("user32.dll")]
			public static extern uint GetQueueStatus(uint flags);

			[DllImport("user32.dll", SetLastError = true)]
			public static extern uint MsgWaitForMultipleObjectsEx(
				uint nCount, IntPtr[] pHandles, uint dwMilliseconds, uint dwWakeMask, uint dwFlags);

			[DllImport("kernel32.dll", SetLastError = true)]
			public static extern uint WaitForMultipleObjects(
				uint nCount, IntPtr[] lpHandles, bool bWaitAll, uint dwMilliseconds);

			[DllImport("kernel32.dll", SetLastError = true)]
			public static extern uint WaitForSingleObject(IntPtr hHandle, uint dwMilliseconds);

			[DllImport("kernel32.dll", SetLastError = true)]
			public static extern IntPtr CreateEvent(IntPtr lpEventAttributes, bool bManualReset, bool bInitialState, string lpName);

			[DllImport("kernel32.dll", SetLastError = true)]
			[return: MarshalAs(UnmanagedType.Bool)]
			public static extern bool SetEvent(IntPtr hEvent);

			[DllImport("kernel32.dll", SetLastError = true)]
			[return: MarshalAs(UnmanagedType.Bool)]
			public static extern bool CloseHandle(IntPtr hObject);
		}
		#endregion
	}
	#endregion

	#region Pumping Test
	/// <summary>
	/// Test Windows message pumping
	/// </summary>
	static class PumpingTest
	{
		// Based on: http://stackoverflow.com/questions/21226600/cowaitformultiplehandles-api-doesnt-behave-as-documented
		// Start and run an STA thread
		public static async Task RunAsync()
		{
			using (var staThread = new Noseratio.ThreadAffinity.ThreadWithAffinityContext(staThread: true, pumpMessages: true))
			{
				Console.WriteLine("Initial thread #" + Thread.CurrentThread.ManagedThreadId);
				await staThread.Run(async () =>
				{
					Console.WriteLine("On STA thread #" + Thread.CurrentThread.ManagedThreadId);
					// create a simple Win32 window
					IntPtr hwnd = CreateTestWindow();

					// Post some WM_TEST messages
					Console.WriteLine("Post some WM_TEST messages...");
					NativeMethods.PostMessage(hwnd, NativeMethods.WM_TEST, new IntPtr(1), IntPtr.Zero);
					NativeMethods.PostMessage(hwnd, NativeMethods.WM_TEST, new IntPtr(2), IntPtr.Zero);
					NativeMethods.PostMessage(hwnd, NativeMethods.WM_TEST, new IntPtr(3), IntPtr.Zero);
					Console.WriteLine("Press Enter to continue...");
					await ReadLineAsync();

					Console.WriteLine("After await, thread #" + Thread.CurrentThread.ManagedThreadId);
					Console.WriteLine("Pending messages in the queue: " + (NativeMethods.GetQueueStatus(0x1FF) >> 16 != 0));

					Console.WriteLine("Exiting STA thread #" + Thread.CurrentThread.ManagedThreadId);
				}, CancellationToken.None);
			}
			Console.WriteLine("Current thread #" + Thread.CurrentThread.ManagedThreadId);
		}

		// Helpers

		// create a window to handle messages
		static IntPtr CreateTestWindow()
		{
			// Create a simple Win32 window 
			var hwndStatic = NativeMethods.CreateWindowEx(0, "Static", String.Empty, NativeMethods.WS_POPUP,
				0, 0, 0, 0, NativeMethods.HWND_MESSAGE, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);

			// subclass it with a custom WndProc
			IntPtr prevWndProc = IntPtr.Zero;

			NativeMethods.WndProc newWndProc = (hwnd, msg, wParam, lParam) =>
			{
				if (msg == NativeMethods.WM_TEST)
					Console.WriteLine("WM_TEST processed: " + wParam);
				return NativeMethods.CallWindowProc(prevWndProc, hwnd, msg, wParam, lParam);
			};

			prevWndProc = NativeMethods.SetWindowLong(hwndStatic, NativeMethods.GWL_WNDPROC,
				Marshal.GetFunctionPointerForDelegate(newWndProc));
			if (prevWndProc == IntPtr.Zero)
				throw new ApplicationException();

			return hwndStatic;
		}

		// call Console.ReadLine on a pool thread
		static Task<string> ReadLineAsync()
		{
			return Task.Run(() => Console.ReadLine());
		}

		// get Win32 waitable handle of Task object
		static IntPtr AsUnmanagedHandle(this Task task)
		{
			return ((IAsyncResult)task).AsyncWaitHandle.SafeWaitHandle.DangerousGetHandle();
		}

		// Interop
		static class NativeMethods
		{
			[DllImport("user32")]
			public static extern IntPtr SetWindowLong(IntPtr hwnd, int nIndex, IntPtr dwNewLong);

			[DllImport("user32")]
			public static extern IntPtr CallWindowProc(IntPtr lpPrevWndFunc, IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

			[DllImport("user32.dll")]
			public static extern IntPtr CreateWindowEx(
				uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
				int x, int y, int nWidth, int nHeight,
				IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

			[DllImport("user32.dll")]
			public static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

			[DllImport("user32.dll")]
			public static extern int MessageBox(IntPtr hwnd, string text, String caption, int options);

			[DllImport("ole32.dll", SetLastError = true)]
			public static extern uint CoWaitForMultipleHandles(uint dwFlags, uint dwTimeout,
			   int cHandles, IntPtr[] pHandles, out uint lpdwindex);

			[DllImport("user32.dll")]
			public static extern uint GetQueueStatus(uint flags);

			[UnmanagedFunctionPointer(CallingConvention.StdCall)]
			public delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

			public static IntPtr HWND_MESSAGE = new IntPtr(-3);

			public const int GWL_WNDPROC = -4;
			public const uint WS_POPUP = 0x80000000;

			public const uint WM_USER = 0x0400;
			public const uint WM_TEST = WM_USER + 1;

			public const uint COWAIT_WAITALL = 1;
			public const uint COWAIT_ALERTABLE = 2;
			public const uint COWAIT_INPUTAVAILABLE = 4;
			public const uint COWAIT_DISPATCH_CALLS = 8;
			public const uint COWAIT_DISPATCH_WINDOW_MESSAGES = 0x10;

			public const uint RPC_S_CALLPENDING = 0x80010115;

			public const uint WAIT_TIMEOUT = 0x00000102;
			public const uint WAIT_FAILED = 0xFFFFFFFF;
			public const uint WAIT_OBJECT_0 = 0;
			public const uint WAIT_ABANDONED_0 = 0x00000080;
			public const uint WAIT_IO_COMPLETION = 0x000000C0;

			public const uint INFINITE = 0xFFFFFFFF;
		}
	}
	#endregion

	#region Program
	/// <summary>
	/// The test console app
	/// </summary>
	class Program
	{
		/// <summary>
		/// Execute a series of "await Task.Delay(delay)" 
		/// and verifies the continuation happens on the same thread
		/// </summary>
		static async Task SubTestAsync(int id, int steps)
		{
			var threadId = Thread.CurrentThread.ManagedThreadId;
			Console.WriteLine("Task #" + id + " stared, thread: " + threadId);

			// yield to the current sync. context
			await Task.Yield();
			// to be continued on the same thread!
			if (threadId != Thread.CurrentThread.ManagedThreadId)
				throw new TaskSchedulerException();

			// a loop of async delays
			for (var i = 1; i <= steps; i++)
			{
				var delay = i * 100;
				await Task.Delay(delay);
				Console.WriteLine("Task #" + id + " after 'await Task.Delay(" + delay + ")', thread: " + Thread.CurrentThread.ManagedThreadId);

				// to be continued on the same thread!
				if (threadId != Thread.CurrentThread.ManagedThreadId)
					throw new TaskSchedulerException();
			}
		}

		/// <summary>
		/// Run a set of SubTestAsync tasks in parallel using ThreadAffinityTaskScheduler
		/// </summary>
		static async Task TestAsync()
		{
			using (var scheduler = new ThreadAffinityTaskScheduler(numberOfThreads: 3))
			{
				var tasks = Enumerable.Range(1, 5).Select(i =>
					scheduler.Run(() => SubTestAsync(id: i, steps: 10),
						CancellationToken.None));

				await Task.WhenAll(tasks);
			}
		}

		/// <summary>
		/// Console app entry point
		/// </summary>
		static void Main(string[] args)
		{
			// test affinity with multiple apartments
			//TestAsync().Wait();

			// test mesage puming in singe apartments
			PumpingTest.RunAsync().Wait();

			Console.WriteLine("Press any key to exit");
			Console.ReadLine();
		}
	}
	#endregion
}

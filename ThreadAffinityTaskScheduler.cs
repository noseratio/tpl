using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Noseratio.ThreadAffinityTaskScheduler
{
    /// <summary>
    /// Provides a pool of single-threaded apartments.
    /// Each apartment provides asynchronous continuation after `await` on the same thread, 
    /// via SingleThreadSynchronizationContext object
    /// 
    /// Partially based on StaTaskScheduler from http://blogs.msdn.com/b/pfxteam/archive/2010/04/07/9990421.aspx
    /// MIT License, (c) 2014 by Noseratio - http://stackoverflow.com/users/1768303/noseratio
    /// </summary>
    public sealed class ThreadAffinityTaskScheduler : TaskScheduler, IDisposable
    {
        /// <summary>A simple callback interface to execute an item (used with BlockingCollection).</summary>
        public interface IExecuteItem
        {
            void Execute();
        }

        /// <summary>Stores the queued tasks to be executed by one of our apartments.</summary>
        private BlockingCollection<IExecuteItem> _tasks;
        /// <summary>The Cancellation Token Source object to request the termination.</summary>
        private readonly CancellationTokenSource _terminationCts;
        /// <summary>The list of SingleThreadSynchronizationContext objects to represent apartments (threads).</summary>
        private readonly List<SingleThreadSynchronizationContext> _contexts;

        /// <summary>Initializes a new instance of the ThreadAffinityTaskScheduler class with the specified concurrency level.</summary>
        /// <param name="numberOfThreads">The number of threads that should be created and used by this scheduler.</param>
        public ThreadAffinityTaskScheduler(int numberOfThreads)
        {
            // Validate arguments
            if (numberOfThreads < 1)
                throw new ArgumentOutOfRangeException("numberOfThreads");

            // Initialize the tasks collection
            _tasks = new BlockingCollection<IExecuteItem>();

            // CTS to cancel task dispatching 
            _terminationCts = new CancellationTokenSource();

            // Create the contexts (threads) to be used by this scheduler
            _contexts = Enumerable.Range(0, numberOfThreads).Select(i =>
            {
                // use TCS to return SingleThreadSynchronizationContext to the task scheduler
                var tcs = new TaskCompletionSource<SingleThreadSynchronizationContext>();

                var thread = new Thread(() =>
                {
                    // create and install on the current thread an instance of SingleThreadSynchronizationContext
                    var context = new SingleThreadSynchronizationContext();
                    SynchronizationContext.SetSynchronizationContext(context);
                    tcs.SetResult(context); // the sync. context is ready, return it to the task scheduler

                    // take IExecuteItem objects from both the thread's collection and the Task Scheduler's collection
                    var pendingItems = new BlockingCollection<IExecuteItem>[] { context.Actions, _tasks };
                    try
                    {
                        // the thread's core task dispatching loop, terminate-able with _terminationCts
                        while (true)
                        {
                            IExecuteItem executeItem;
                            // TakeFromAny is blocking if no items
                            BlockingCollection<IExecuteItem>.TakeFromAny(pendingItems,
                                out executeItem,
                                _terminationCts.Token);
                            // execute a Task queued by ThreadAffinityTaskScheduler.QueueTask
                            // or a callback queued by SingleThreadSynchronizationContext.Post
                            executeItem.Execute();
                        };
                    }
                    catch (OperationCanceledException)
                    {
                        // ignore OperationCanceledException exceptions when terminating
                        if (!_terminationCts.IsCancellationRequested)
                            throw;
                    }
                });

                thread.IsBackground = true;
                thread.Start();
                return tcs.Task.Result;
            }).ToList();
        }

        /// <summary>Queues a Task to be executed by this scheduler.</summary>
        /// <param name="task">The task to be executed.</param>
        protected override void QueueTask(Task task)
        {
            // Push the task into the blocking collection of tasks
            _tasks.Add(new TaskItem(task, this.TryExecuteTask));
        }

        /// <summary>Provides a list of the scheduled tasks for the debugger to consume.</summary>
        /// <returns>An enumerable of all tasks currently scheduled.</returns>
        protected override IEnumerable<Task> GetScheduledTasks()
        {
            // Serialize the contents of the blocking collection of tasks for the debugger
            return _tasks.Select(t => ((TaskItem)t).Task).ToArray();
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
            get { return _contexts.Count; }
        }

        /// <summary>
        /// Request the termination and so the cleanup.
        /// This method blocks until all threads successfully shutdown.
        /// </summary>
        public void Dispose()
        {
            if (_tasks != null)
            {
                // Request the cancellation
                _terminationCts.Cancel();

                // Dispose each context
                foreach (var context in _contexts)
                    context.Dispose();

                // Cleanup
                _tasks.Dispose();
                _tasks = null;
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

        /// <summary>
        /// A simple container to store and execute a task via IExecuteItem.Execute
        /// </summary>
        class TaskItem : IExecuteItem
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

        /// <summary>
        /// A helper Synchronization Context class to post continuations on the same thread
        /// </summary>
        public class SingleThreadSynchronizationContext : SynchronizationContext, IDisposable
        {
            private BlockingCollection<IExecuteItem> _actions;
            public Thread Thread { get; private set; }

            public SingleThreadSynchronizationContext()
            {
                _actions = new BlockingCollection<IExecuteItem>();
                this.Thread = Thread.CurrentThread;
            }

            public BlockingCollection<IExecuteItem> Actions
            {
                get { return _actions; }
            }

            public override void Post(SendOrPostCallback d, object state)
            {
                _actions.Add(new ActionItem(() => d(state)));
            }

            public override void Send(SendOrPostCallback d, object state)
            {
                //TODO: currently we don't support (and expect) synchronous callbacks
                throw new NotImplementedException();
            }

            public void Dispose()
            {
                if (_actions != null)
                {
                    // Wait the thread to finish processing tasks
                    this.Thread.Join();

                    // Cleanup
                    _actions.Dispose();
                    _actions = null;
                }
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
        };
    }

    /// <summary>
    /// The test console app
    /// </summary>
    class Program
    {
        /// <summary>
        /// Execute a series of "await Task.Delay(delay)" and
        /// verifies the continuation happens on the same thread
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
            var scheduler = new ThreadAffinityTaskScheduler(numberOfThreads: 5);

            var tasks = Enumerable.Range(1, 15).Select(i =>
                scheduler.Run(() => SubTestAsync(id: i, steps: 10),
                    CancellationToken.None));

            await Task.WhenAll(tasks);
            scheduler.Dispose();
        }

        // Main
        static void Main(string[] args)
        {
            TestAsync().Wait();
            Console.WriteLine("Press any key to exit");
            Console.ReadLine();
        }
    }
}

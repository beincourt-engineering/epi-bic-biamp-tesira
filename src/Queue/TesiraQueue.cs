using System;
using System.Threading;
using Crestron.SimplSharp;
using PepperDash.Core.Logging;

namespace Pepperdash.Essentials.Plugins.DSP.Biamp.Tesira.Queue
{
    public class TesiraQueue
    {
        private ExpressLaneQueue<QueuedCommand> LocalQueue { get; set; }

        private TesiraDsp Parent { get; set; }

        public bool CommandQueueInProgress { get; set; }

        /// <summary>
        /// Gets the number of items in the queue
        /// </summary>
        public int Count => LocalQueue?.Count ?? 0;

        /// <summary>
        /// Gets whether the queue is empty
        /// </summary>
        public bool IsEmpty => LocalQueue?.IsEmpty ?? true;

        private QueuedCommand lastDequeued;

        private readonly object lockObject = new object();

        /// <summary>
        /// How long a sent command may wait for its reply before the queue moves on. Replies normally
        /// arrive within milliseconds; this is a backstop so that a reply the parser does not
        /// recognise, or one lost with the connection, cannot stop every later command.
        /// </summary>
        public static readonly TimeSpan ResponseTimeout = TimeSpan.FromSeconds(10);

        private Timer responseTimer;

        /// <summary>
        /// Constructor for Tesira Queue
        /// </summary>
        /// <param name="queueSize">Most commands each lane holds (express and normal)</param>
        /// <param name="parent">Parent TesiraDsp Class</param>
        public TesiraQueue(int queueSize, TesiraDsp parent)
        {
            LocalQueue = new ExpressLaneQueue<QueuedCommand>(queueSize);
            Parent = parent;
            CommandQueueInProgress = false;
        }

        /// <summary>
        /// Dequeue from TesiraQueue and process queue responses
        /// </summary>
        /// <param name="response">Command String comparator for QueuedCommand</param>
        public void HandleResponse(string response)
        {
            lock (lockObject)
            {
                Parent.LogVerbose("[HandleResponse] Command Queue {state} in progress.", CommandQueueInProgress ? "is" : "is not");

                if (lastDequeued?.ControlPoint != null)
                {
                    Parent.LogVerbose("[HandleResponse] Response Received for parsing: '{response}'. Command: '{outgoingCommand}'", response, lastDequeued.Command);

                    lastDequeued.ControlPoint.ParseGetMessage(lastDequeued.AttributeCode, response);
                }
                else
                {
                    Parent.LogVerbose("[HandleResponse] Incoming Response: '{response}'. No Controlpoint waiting for response", response);
                }

                lastDequeued = null;
                CancelResponseTimer();

                if (LocalQueue.IsEmpty)
                {
                    Parent.LogVerbose("[HandleResponse] Command Queue is empty. Ending queue processing.");
                    CommandQueueInProgress = false;
                    return;
                }

                SendNextQueuedCommand();
            }
        }

        /// <summary>
        /// Adds a command from a child module to the queue
        /// </summary>
        /// <param name="commandToEnqueue">Command object from child module</param>
        public void EnqueueCommand(QueuedCommand commandToEnqueue)
        {
            lock (lockObject)
            {
                Parent.LogVerbose("[EnqueueCommand] Attempting to enqueue command for {controlPoint} with priority {priority}", commandToEnqueue.ControlPoint?.Key ?? "no control point", commandToEnqueue.Priority);
                Parent.LogVerbose("[EnqueueCommand] Command Queue {state} in progress.", CommandQueueInProgress ? "is" : "is not");

                // Never block while holding the lock: a full queue drops the command instead.
                if (!LocalQueue.TryEnqueue(commandToEnqueue, commandToEnqueue.Express))
                {
                    Parent.LogWarning("[EnqueueCommand] Command queue is full ({count} items); dropping '{command}'", LocalQueue.Count, commandToEnqueue.Command);
                    return;
                }

                Parent.LogVerbose("[EnqueueCommand] Command Enqueued: '{command}'.  CommandQueue has {count} items", commandToEnqueue.Command, LocalQueue.Count);

                if (CommandQueueInProgress) return;

                if (lastDequeued == null)
                {
                    Parent.LogVerbose("[EnqueueCommand] Sending Next Queued Command");
                    SendNextQueuedCommand();
                }
            }
        }

        /// <summary>
        /// Adds a raw string command to the queue
        /// </summary>
        /// <param name="command">String to enqueue</param>
        /// <param name="sendLineRaw">Send command without appending delimiter</param>
        /// <param name="priority">Command priority (defaults to Low priority)</param>
        public void EnqueueCommand(string command, bool sendLineRaw = false, int priority = (int)CommandPriority.Low)
        {
            EnqueueCommand(new QueuedCommand(command, null, null, sendLineRaw: sendLineRaw, priority: priority));
        }

        /// <summary>
        /// Sends the next queued command to the DSP
        /// </summary>
        public void SendNextQueuedCommand()
        {
            lock (lockObject)
            {
                Parent.LogVerbose("[SendNextQueuedCommand] Attempting to send a queued command");

                if (LocalQueue.IsEmpty)
                {
                    Parent.LogVerbose("[SendNextQueuedCommand] Command Queue is empty. No command to send.");
                    CommandQueueInProgress = false;
                    return;
                }

                Parent.LogVerbose("[SendNextQueuedCommand] Command Queue {state} in progress.", CommandQueueInProgress ? "is" : "is not");

                if (!Parent.Communication.IsConnected)
                {
                    Parent.LogVerbose("[SendNextQueuedCommand] Unable to send queued command. Tesira Disconnected");
                    return;
                }

                CommandQueueInProgress = true;

                if (!LocalQueue.TryDequeue(out lastDequeued))
                {
                    Parent.LogError("[SendNextQueuedCommand] Failed to dequeue command despite queue not being empty");
                    CommandQueueInProgress = false;
                    return;
                }

                Parent.LogVerbose("[SendNextQueuedCommand] Sending Line {line}. ControlPoint: {controlPoint}", lastDequeued.Command, lastDequeued.ControlPoint?.Key ?? "no control point");

                StartResponseTimer(lastDequeued);

                if (lastDequeued.SendLineRaw)
                    Parent.SendLineRaw(lastDequeued.Command, lastDequeued.BypassTxQueue);
                else
                    Parent.SendLine(lastDequeued.Command, lastDequeued.BypassTxQueue);
            }
        }

        private void StartResponseTimer(QueuedCommand sent)
        {
            CancelResponseTimer();
            responseTimer = new Timer(_ => OnResponseTimeout(sent), null, ResponseTimeout, System.Threading.Timeout.InfiniteTimeSpan);
        }

        private void CancelResponseTimer()
        {
            responseTimer?.Dispose();
            responseTimer = null;
        }

        private void OnResponseTimeout(QueuedCommand sent)
        {
            lock (lockObject)
            {
                // Only if that command is still the one waiting: its reply may have arrived as the
                // timer fired, and the queue may have been cleared or moved on since.
                if (!ReferenceEquals(lastDequeued, sent))
                    return;

                Parent.LogWarning("[ResponseTimeout] No reply to '{command}' within {seconds}s; moving on to the next command", sent.Command, ResponseTimeout.TotalSeconds);

                lastDequeued = null;
                CancelResponseTimer();
                CommandQueueInProgress = false;

                if (!LocalQueue.IsEmpty)
                    SendNextQueuedCommand();
            }
        }

        /// <summary>
        /// Clears the TesiraQueue
        /// </summary>
        public void Clear()
        {
            lock (lockObject)
            {
                if (LocalQueue == null) return;
                LocalQueue.Clear();
                lastDequeued = null;
                CancelResponseTimer();
                CommandQueueInProgress = false;
            }
        }

    }

}
﻿using System;
using System.Threading;

using NetMQ;

using MajordomoProtocol.Contracts;

namespace MajordomoProtocol
{
    public class MDPWorker : IMajordomoWorker, IDisposable
    {
        // according to MDP identification for a worker messageincluding the version 
        // "MDP"    -> Majordomo Protocol
        // "W"      -> Worker
        // "01"     -> Version 0.1
        private const string _MDP_WORKER = "MDPW01";

        private const int _HEARTBEAT_LIVELINESS = 3;    // indicates the remaining "live" for the worker

        private readonly NetMQContext m_ctx;
        private readonly string m_brokerAddress;     // the broker address to connect to
        private readonly string m_serviceName;       // the name of the service the worker offers
        private NetMQSocket m_worker;       // the worker socket itself -MDP requires to use DEALER
        private DateTime m_heartbeatAt;     // when to send HEARTBEAT
        private int m_liveliness;           // how many attempts are left
        private int m_expectReply;         // will be 0 at start
        private NetMQFrame m_returnIdentity;// the return identity if any
        private bool m_exit;               // a flag for exiting the worker
        private bool m_connected;          // a flag to signal whether worker is connected to broker or not
        private NetMQMessage m_request;     // used to collect the request received from the ReceiveReady event handler

        /// <summary>
        ///     sen a heartbeat every specified milliseconds
        /// </summary>
        public TimeSpan HeartbeatDelay { get; set; }

        /// <summary>
        ///     delay in milliseconds between reconnets
        /// </summary>
        public TimeSpan ReconnectDelay { get; set; }

        /// <summary>
        ///     broadcast logging information via this event
        /// </summary>
        public event EventHandler<LogInfoEventArgs> LogInfoReady;

        /// <summary>
        ///     create worker with standart parameter
        ///     HeartbeatDelay == 2500 milliseconds
        ///     ReconnectDelay == 2500 milliseconds
        ///     Verbose == false
        /// </summary>
        private MDPWorker ()
        {
            m_ctx = NetMQContext.Create ();

            HeartbeatDelay = TimeSpan.FromMilliseconds (2500);
            ReconnectDelay = TimeSpan.FromMilliseconds (2500);
            m_exit = false;
            m_connected = false;
        }

        /// <summary>
        ///     creates worker with standrat parameter and 
        ///     set the broker's address and the service name for the worker
        /// </summary>
        /// <param name="brokerAddress">the address the worker can connect to the broker at</param>
        /// <param name="serviceName">the service the worker offers</param>
        /// <exception cref="ArgumentNullException">The address of the broker must not be null, empty or whitespace!</exception>
        /// <exception cref="ArgumentNullException">The name of the service must not be null, empty or whitespace!</exception>
        /// <remarks>
        ///     create worker with standart parameter
        ///     HeartbeatDelay == 2500 milliseconds
        ///     ReconnectDelay == 2500 milliseconds
        ///     Verbose == false
        /// </remarks>
        public MDPWorker (string brokerAddress, string serviceName)
            : this ()
        {
            if (string.IsNullOrWhiteSpace (brokerAddress))
                throw new ArgumentNullException ("brokerAddress",
                                                 "The address of the broker must not be null, empty or whitespace!");
            if (string.IsNullOrWhiteSpace (serviceName))
                throw new ArgumentNullException ("serviceName",
                                                 "The name of the service must not be null, empty or whitespace!");

            m_brokerAddress = brokerAddress;
            m_serviceName = serviceName;
        }

        /// <summary>
        ///     initially sends a READY message to the broker upon connection
        ///     and waits for a request to come
        /// </summary>
        /// <param name="reply">teh reply to send</param>
        /// <returns>the request to process</returns>
        /// <remarks>
        ///     if it is the initial call to Receive - reply must be <c>null</c> in order to 
        ///     send READY and subsequently wait for a request.
        ///     reply == <c>null</c> will bypass the sending of a message!
        /// </remarks>
        public NetMQMessage Receive (NetMQMessage reply)
        {
            if (!m_connected)
                Connect ();

            // since Connect send the READY we are waiting after a Connect for a
            // REQ and must skip the REPLY step
            // if however the Connect has not been called than we have received
            // and processed a REQ and must send a REP and at one must be pending
            if (!ReferenceEquals (reply, null) && m_expectReply != 0)
            {
                if (ReferenceEquals (m_returnIdentity, null) || m_returnIdentity.BufferSize == 0)
                    throw new ApplicationException ("A malformed reply has been provided");

                var message = Wrap (reply, m_returnIdentity);

                m_worker.SendMessage (message);
            }

            m_expectReply = 1;
            // now wait for the next request
            while (!m_exit)
            {
                if (m_worker.Poll (HeartbeatDelay))
                {
                    // ProcessReceiveReady will set m_request only if a request arrived
                    if (!ReferenceEquals (m_request, null))
                        return m_request;
                }
                else
                {
                    if (--m_liveliness == 0)
                    {
                        OnLogInfoReady (new LogInfoEventArgs { LogInfo = "[WORKER INFO] disconnected from broker - retrying ..." });
                        // wait before reconnecting
                        Thread.Sleep (HeartbeatDelay);
                        // reconnect
                        Connect ();
                    }

                    // if it is time to send a heartbeat to broker
                    if (DateTime.UtcNow >= m_heartbeatAt)
                    {
                        Send (MDPCommand.Heartbeat, null, null);
                        // set new point in time for sending the next heartbeat
                        m_heartbeatAt = DateTime.UtcNow + HeartbeatDelay;
                    }
                }
            }

            OnLogInfoReady (new LogInfoEventArgs { LogInfo = "[WORKER] abandoning worker due to errors!" });

            m_worker.Dispose ();
            m_ctx.Dispose ();

            return null;
        }

        /// <summary>
        ///     if a logging information shall be broadcasted
        ///     raise the event if someone is listening
        /// </summary>
        /// <param name="e">the wrapped logging information</param>
        protected virtual void OnLogInfoReady (LogInfoEventArgs e)
        {
            var handler = LogInfoReady;

            if (handler != null)
                handler (this, e);
        }

        /// <summary>
        ///     upon arrival of a message process it
        ///     and set the request variable accordingly
        /// </summary>
        protected virtual void ProcessReceiveReady (object sender, NetMQSocketEventArgs e)
        {
            // a request has arrived process it
            var request = m_worker.ReceiveMessage ();

            OnLogInfoReady (new LogInfoEventArgs { LogInfo = string.Format ("[WORKER] request received {0}", request) });
            // make sure that we have not yet a valid request!
            m_request = null;
            // any message from broker is treated as heartbeat(!) since communication exists
            m_liveliness = _HEARTBEAT_LIVELINESS;
            // don't try to handle errors
            if (request.FrameCount < 3)
                throw new ApplicationException ("Malformed request received");

            var empty = request.Pop ();

            if (empty != NetMQFrame.Empty)
                throw new ApplicationException ("First frame must be an empty frame!");

            var header = request.Pop ();

            if (header.ConvertToString () != _MDP_WORKER)
                throw new ApplicationException ("Invalid protocol header received");

            var command = request.Pop ();

            // MDP command is one byte!
            switch (command.Buffer[0])
            {
                case (byte) MDPCommand.Request:
                    // save as many addresses as there are until we hit an empty frame 
                    // - for simplicity assume it is just one
                    m_returnIdentity = Unwrap (request);
                    // set the call global variable in order to return the request to caller
                    m_request = request;
                    break;
                case (byte) MDPCommand.Heartbeat:
                    // reset the liveliness of the broker
                    m_liveliness = _HEARTBEAT_LIVELINESS;
                    break;
                case (byte) MDPCommand.Disconnect:
                    // reconnect the worker
                    Connect ();
                    break;
                case (byte) MDPCommand.Kill:
                    // stop working you worker you
                    m_exit = true;
                    break;
                default:
                    OnLogInfoReady (new LogInfoEventArgs { LogInfo = "[WORKER ERROR] invalid command received" });
                    break;
            }
        }

        /// <summary>
        ///     connect or re-connect to the broker
        /// </summary>
        private void Connect ()
        {
            // if the socket exists dispose it and re-create one
            if (!ReferenceEquals (m_worker, null))
                m_worker.Dispose ();

            m_worker = m_ctx.CreateDealerSocket ();
            // hook up the received message processing method before the socket is connected
            m_worker.ReceiveReady += ProcessReceiveReady;

            m_worker.Connect (m_brokerAddress);
            // signal that worker is connected
            m_connected = true;

            OnLogInfoReady (new LogInfoEventArgs
                            {
                                LogInfo = string.Format ("[WORKER] connecting to broker at {0}", m_brokerAddress)
                            });

            // send READY to broker since worker is connected
            Send (MDPCommand.Ready, m_serviceName, null);
            // reset liveliness to active broker
            m_liveliness = _HEARTBEAT_LIVELINESS;
            // set point in time for next heatbeat
            m_heartbeatAt = DateTime.UtcNow + HeartbeatDelay;
        }

        /// <summary>
        ///     send a message to broker
        ///     if no message provided create a new empty one
        ///     prepend the message with the MDP prologue
        /// </summary>
        /// <param name="mdpCommand">MDP command</param>
        /// <param name="data">data to be sent</param>
        /// <param name="message">the message to send</param>
        private void Send (MDPCommand mdpCommand, string data, NetMQMessage message)
        {
            var msg = ReferenceEquals (message, null) ? new NetMQMessage () : new NetMQMessage (message);
            // protocol envelope according to MDP
            // last frame is the data if available
            if (!ReferenceEquals (data, null))
            {
                // data could be multiple whitespaces or even empty(!)
                msg.Push (data);
            }
            // set MDP command
            msg.Push (((byte) mdpCommand).ToString ());
            // set MDP ID
            msg.Push (_MDP_WORKER);
            // set MDP empty frame as separator
            msg.Push (NetMQFrame.Empty);

            OnLogInfoReady (new LogInfoEventArgs { LogInfo = string.Format ("[WORKER] sending {0} to broker", msg) });

            m_worker.SendMessage (msg);
        }

        /// <summary>
        ///     prepend the message with an empty frame as separator and a frame
        /// </summary>
        /// <returns>new message with wrapped content</returns>
        private NetMQMessage Wrap (NetMQMessage msg, NetMQFrame frame)
        {
            var result = new NetMQMessage (msg);

            msg.Push (NetMQFrame.Empty);            // according to MDP an empty frame is the separator
            msg.Push (frame);                       // the return address

            return result;
        }

        /// <summary>
        ///     strip the message from the first frame and if empty the following frame as well
        /// </summary>
        /// <returns>the first frame of the message</returns>
        private NetMQFrame Unwrap (NetMQMessage msg)
        {
            var result = msg.Pop ();

            if (msg.First == NetMQFrame.Empty)
                msg.Pop ();

            return result;
        }

        #region IDisposable

        public void Dispose ()
        {
            Dispose (true);
            GC.SuppressFinalize (this);
        }

        protected virtual void Dispose (bool disposing)
        {
            if (disposing)
            {
                m_worker.Dispose ();
                m_ctx.Dispose ();
            }
            // get rid of unmanaged resources
        }

        #endregion IDisposable

    }
}

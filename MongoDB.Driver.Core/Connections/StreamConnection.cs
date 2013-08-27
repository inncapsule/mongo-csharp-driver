﻿/* Copyright 2010-2013 10gen Inc.
*
* Licensed under the Apache License, Version 2.0 (the "License");
* you may not use this file except in compliance with the License.
* You may obtain a copy of the License at
*
* http://www.apache.org/licenses/LICENSE-2.0
*
* Unless required by applicable law or agreed to in writing, software
* distributed under the License is distributed on an "AS IS" BASIS,
* WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
* See the License for the specific language governing permissions and
* limitations under the License.
*/

using System;
using System.Diagnostics;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using MongoDB.Driver.Core.Diagnostics;
using MongoDB.Driver.Core.Events;
using MongoDB.Driver.Core.Protocol.Messages;
using MongoDB.Driver.Core.Support;
using MongoDB.Driver.Core.Security;

namespace MongoDB.Driver.Core.Connections
{
    internal sealed class StreamConnection : ConnectionBase
    {
        // private static fields
        private static readonly TraceSource __trace = MongoTraceSources.Connections;

        // private fields
        private readonly DnsEndPoint _dnsEndPoint;
        private readonly IEventPublisher _events;
        private readonly StreamConnectionSettings _settings;
        private readonly IStreamFactory _streamFactory;
        private readonly string _toStringDescription;
        private bool _disposed;
        private State _state;
        private Stream _stream;

        // constructors
        /// <summary>
        /// Initializes a new instance of the <see cref="StreamConnection" /> class.
        /// </summary>
        /// <param name="settings">The settings.</param>
        /// <param name="dnsEndPoint">The DNS end point.</param>
        /// <param name="streamFactory">The stream factory.</param>
        /// <param name="events">The events.</param>
        public StreamConnection(StreamConnectionSettings settings, DnsEndPoint dnsEndPoint, IStreamFactory streamFactory, IEventPublisher events)
        {
            Ensure.IsNotNull("settings", settings);
            Ensure.IsNotNull("dnsEndPoint", dnsEndPoint);
            Ensure.IsNotNull("streamFactory", streamFactory);
            Ensure.IsNotNull("events", events);

            _dnsEndPoint = dnsEndPoint;
            _events = events;
            _settings = settings;
            _streamFactory = streamFactory;
            _state = State.Initial;
            _toStringDescription = string.Format("conn#{0}-{1}", IdGenerator<IConnection>.GetNextId(), dnsEndPoint);

            __trace.TraceVerbose("{0}: {1}", _toStringDescription, _settings);
        }

        // public properties
        /// <summary>
        /// Gets the address.
        /// </summary>
        public override DnsEndPoint DnsEndPoint
        {
            get
            {
                ThrowIfDisposed();
                return _dnsEndPoint;
            }
        }

        /// <summary>
        /// Gets a value indicating whether this connection is open.
        /// </summary>
        public override bool IsOpen
        {
            get { return _state == State.Open; }
        }

        // public methods
        /// <summary>
        /// Opens the connection.
        /// </summary>
        public override void Open()
        {
            ThrowIfDisposed();
            if (_state == State.Initial)
            {
                try
                {
                    __trace.TraceInformation("{0}: opened.", _toStringDescription);
                    _stream = _streamFactory.Create(_dnsEndPoint);
                    _state = State.Open;
                    _events.Publish(new ConnectionOpenedEvent(this));
                }
                catch (SocketException ex)
                {
                    __trace.TraceError(ex, "{0}: failed to open.", _toStringDescription);
                    HandleException(ex);
                    if (ex.SocketErrorCode == SocketError.TimedOut)
                    {
                        throw new MongoConnectTimeoutException(string.Format("Timed out opening a connection with {0}", _dnsEndPoint), ex);
                    }
                    else
                    {
                        throw new MongoSocketException("Error opening socket.", ex);
                    }
                }
                catch (Exception ex)
                {
                    __trace.TraceError(ex, "{0}: failed to open.", _toStringDescription);
                    HandleException(ex);
                    throw new MongoDriverException("Unable to open connection.", ex);
                }
            }

            foreach (var credential in _settings.Credentials)
            {
                Authenticate(credential);
            }
        }

        /// <summary>
        /// Receives a message.
        /// </summary>
        /// <returns>The reply from the server.</returns>
        public override ReplyMessage Receive()
        {
            ThrowIfDisposed();
            ThrowIfNotOpen();

            try
            {
                var reply = ReplyMessage.ReadFrom(_stream);
                __trace.TraceVerbose("{0}: received message#{1} with {2} bytes.", _toStringDescription, reply.ResponseTo, reply.Length);
                _events.Publish(new ConnectionMessageReceivedEvent(this, reply));

                return reply;
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    __trace.TraceWarning(ex, "{0}: timed out receiving message. Timeout = {1} milliseconds", _toStringDescription, _stream.ReadTimeout);
                    HandleException(ex);
                    throw new MongoSocketReadTimeoutException(string.Format("Timed out receiving message. Timeout = {0} milliseconds", _stream.ReadTimeout), ex);
                }
                else
                {
                    __trace.TraceWarning(ex, "{0}: error receiving message.", _toStringDescription);
                    HandleException(ex);
                    throw new MongoSocketException("Error receiving message", ex);
                }
            }
            catch (Exception ex)
            {
                __trace.TraceWarning(ex, "{0}: error receiving message.", _toStringDescription);
                HandleException(ex);
                if (ex is MongoDriverException)
                {
                    throw;
                }
                else
                {
                    throw new MongoDriverException("Error receiving message.", ex);
                }
            }
        }

        /// <summary>
        /// Sends the packet.
        /// </summary>
        public override void Send(IRequestPacket packet)
        {
            Ensure.IsNotNull("packet", packet);

            ThrowIfDisposed();
            ThrowIfNotOpen();

            try
            {
                packet.WriteTo(_stream);
                __trace.TraceVerbose("{0}: sent message#{1} with {2} bytes.", _toStringDescription, packet.LastRequestId, packet.Length);
                _events.Publish(new ConnectionPacketSendingEvent(this, packet));
            }
            catch (SocketException ex)
            {
                if (ex.SocketErrorCode == SocketError.TimedOut)
                {
                    __trace.TraceWarning(ex, "{0}: timed out sending message#{1}. Timeout = {2} milliseconds", _toStringDescription, packet.LastRequestId, _stream.ReadTimeout);
                    HandleException(ex);
                    throw new MongoSocketWriteTimeoutException(string.Format("Timed out sending message#{0}. Timeout = {1} milliseconds", packet.LastRequestId, _stream.ReadTimeout), ex);
                }
                else
                {
                    __trace.TraceWarning(ex, "{0}: error sending message#{1}.", _toStringDescription, packet.LastRequestId);
                    HandleException(ex);
                    throw new MongoSocketException(string.Format("Error sending message #{0}", packet.LastRequestId), ex);
                }
            }
            catch (Exception ex)
            {
                __trace.TraceWarning(ex, "{0}: error sending message#{1}.", _toStringDescription, packet.LastRequestId);
                HandleException(ex);

                if (ex is MongoDriverException)
                {
                    throw;
                }
                else
                {
                    throw new MongoDriverException(string.Format("Error sending message #{0}", packet.LastRequestId), ex);
                }
            }
        }

        /// <summary>
        /// Returns a <see cref="System.String" /> that represents this instance.
        /// </summary>
        /// <returns>A <see cref="System.String" /> that represents this instance.</returns>
        public override string ToString()
        {
            return _toStringDescription;
        }

        // protected methods
        /// <summary>
        /// Releases unmanaged and - optionally - managed resources.
        /// </summary>
        /// <param name="disposing"><c>true</c> to release both managed and unmanaged resources; <c>false</c> to release only unmanaged resources.</param>
        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                __trace.TraceInformation("{0}: closed.", _toStringDescription);
                _events.Publish(new ConnectionClosedEvent(this));
                _state = State.Disposed;
                try { _stream.Close(); }
                catch { } // ignore exceptions
            }
            _disposed = true;
            base.Dispose(disposing);
        }

        // private methods
        private void Authenticate(MongoCredential credential)
        {
            foreach (var protocol in _settings.Protocols)
            {
                if (protocol.CanUse(credential))
                {
                    protocol.Authenticate(this, credential);
                    return;
                }
            }

            var message = string.Format("Unable to find a security protocol to authenticate. The credential for source {0}, username {1} over mechanism {2} could not be authenticated.", credential.Source, credential.Username, credential.Mechanism);
            throw new MongoSecurityException(message);
        }

        private void HandleException(Exception ex)
        {
            // we'll always dispose for any error.
            Dispose();
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(GetType().Name);
            }
        }

        private void ThrowIfNotOpen()
        {
            if (_state != State.Open)
            {
                throw new InvalidOperationException("The connection must be opened before it can be used.");
            }
        }

        // nested classes
        private enum State
        {
            Initial,
            Open,
            Disposed
        }
    }
}

﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Schema;
using Eu.EDelivery.AS4.Builders.Core;
using Eu.EDelivery.AS4.Builders.Internal;
using Eu.EDelivery.AS4.Builders.Security;
using Eu.EDelivery.AS4.Exceptions;
using Eu.EDelivery.AS4.Model.Core;
using Eu.EDelivery.AS4.Model.PMode;
using Eu.EDelivery.AS4.Resources;
using Eu.EDelivery.AS4.Security.Strategies;
using Eu.EDelivery.AS4.Singletons;
using Eu.EDelivery.AS4.Xml;
using NLog;
using Error = Eu.EDelivery.AS4.Model.Core.Error;
using Exception = System.Exception;
using PullRequest = Eu.EDelivery.AS4.Model.Core.PullRequest;
using Receipt = Eu.EDelivery.AS4.Model.Core.Receipt;
using SignalMessage = Eu.EDelivery.AS4.Model.Core.SignalMessage;
using UserMessage = Eu.EDelivery.AS4.Model.Core.UserMessage;

namespace Eu.EDelivery.AS4.Serialization
{
    /// <summary>
    /// Serialize <see cref="AS4Message" /> to a <see cref="Stream" />
    /// </summary>
    public class SoapEnvelopeSerializer : ISerializer
    {
        private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();
        private static readonly XmlWriterSettings DefaultXmlWriterSettings = new XmlWriterSettings
        {
            CloseOutput = false,
            Encoding = new UTF8Encoding(false)
        };
        private static readonly XmlReaderSettings DefaultXmlReaderSettings = new XmlReaderSettings
        {
            Async = true,
            CloseInput = false,
            IgnoreComments = true
        };

        private static XmlSchema _envelopeSchema;

        /// <summary>
        /// Serialize SOAP Envelope to a <see cref="Stream" />
        /// </summary>
        /// <param name="message"></param>
        /// <param name="stream"></param>
        /// <param name="cancellationToken"></param>
        public void Serialize(AS4Message message, Stream stream, CancellationToken cancellationToken)
        {
            Messaging messagingHeader = CreateMessagingHeader(message);

            var builder = new SoapEnvelopeBuilder();

            XmlNode securityHeader = GetSecurityHeader(message);
            if (securityHeader != null)
            {
                builder.SetSecurityHeader(securityHeader);
            }

            SetMultiHopHeaders(builder, message);

            builder.SetMessagingHeader(messagingHeader);
            builder.SetMessagingBody(message.SigningId.BodySecurityId);

            WriteSoapEnvelopeTo(builder.Build(), stream);
        }

        private static Messaging CreateMessagingHeader(AS4Message message)
        {
            var messagingHeader = new Messaging {SecurityId = message.SigningId.HeaderSecurityId};

            if (message.IsSignalMessage) messagingHeader.SignalMessage = AS4Mapper.Map<Xml.SignalMessage[]>(message.SignalMessages);
            else messagingHeader.UserMessage = AS4Mapper.Map<Xml.UserMessage[]>(message.UserMessages);

            if (IsMultiHop(message.SendingPMode))
            {
                messagingHeader.role = Constants.Namespaces.EbmsNextMsh;
                messagingHeader.mustUnderstand1 = true;
                messagingHeader.mustUnderstand1Specified = true;
            }

            return messagingHeader;
        }

        private static bool IsMultiHop(SendingProcessingMode pmode)
        {
            return pmode?.MessagePackaging.IsMultiHop == true;
        }

        private static XmlNode GetSecurityHeader(AS4Message message)
        {
            if (message.SecurityHeader.IsSigned == false && message.SecurityHeader.IsEncrypted == false)
            {
                return null;
            }

            return message.SecurityHeader?.GetXml();
        }

        private static void SetMultiHopHeaders(SoapEnvelopeBuilder builder, AS4Message as4Message)
        {
            // TODO: i'd like to see this refactored.
            // AS4Message could have a property 'IsMultihop', and based on that, the
            // correct multihop-headers could be set.

            // If the AS4Message is a signalmessage, check if the related usermessage
            // was a multihop.
            if (!IsMultiHop(as4Message.SendingPMode) || !as4Message.IsSignalMessage) return;

            var to = new To {Role = Constants.Namespaces.EbmsNextMsh};
            builder.SetToHeader(to);

            string actionValue = as4Message.PrimarySignalMessage.GetActionValue();
            builder.SetActionHeader(actionValue);

            var routingInput = new RoutingInput
            {
                UserMessage = AS4Mapper.Map<RoutingInputUserMessage>(as4Message.PrimarySignalMessage.RelatedUserMessage)
            };

            builder.SetRoutingInput(routingInput);
        }

        private static void WriteSoapEnvelopeTo(XmlNode soapEnvelopeDocument, Stream stream)
        {
            using (XmlWriter writer = XmlWriter.Create(stream, DefaultXmlWriterSettings))
            {
                soapEnvelopeDocument.WriteTo(writer);
            }
        }

        /// <summary>
        /// Parser the SOAP message to a <see cref="AS4Message" />
        /// </summary>
        /// <param name="envelopeStream">RequestStream that contains the SOAP Messaging Header</param>
        /// <param name="contentType"></param>
        /// <param name="token"></param>
        /// <returns><see cref="AS4Message" /> that wraps the User and Signal Messages</returns>
        public async Task<AS4Message> DeserializeAsync(
            Stream envelopeStream,
            string contentType,
            CancellationToken token)
        {
            if (envelopeStream == null) throw new ArgumentNullException(nameof(envelopeStream));

            using (Stream stream = CopyEnvelopeStream(envelopeStream))
            {
                XmlDocument envelopeDocument = LoadXmlDocument(stream);

                // FRGH
                // [Conformance Testing]
                // Temporarely disabled.
                // ValidateEnvelopeDocument(envelopeDocument);
                stream.Position = 0;

                var as4Message = new AS4Message {ContentType = contentType, EnvelopeDocument = envelopeDocument};

                using (XmlReader reader = XmlReader.Create(stream, DefaultXmlReaderSettings))
                {
                    while (await reader.ReadAsync().ConfigureAwait(false))
                    {
                        DeserializeEnvelope(envelopeDocument, as4Message, reader);
                    }
                }

                return as4Message;
            }
        }

        private static Stream CopyEnvelopeStream(Stream envelopeStream)
        {
            Stream stream = new MemoryStream();

            envelopeStream.CopyTo(stream);
            stream.Position = 0;

            return stream;
        }

        private void ValidateEnvelopeDocument(XmlDocument envelopeDocument)
        {
            var schemas = new XmlSchemaSet();
            XmlSchema schema = GetEnvelopeSchema();
            schemas.Add(schema);
            envelopeDocument.Schemas = schemas;

            TryValidateEnvelopeDocument(envelopeDocument);

            Logger.Debug("Valid ebMS Envelope Document");
        }

        private static XmlSchema GetEnvelopeSchema()
        {
            if (_envelopeSchema == null)
            {
                using (var stringReader = new StringReader(Schemas.Soap12))
                {
                    _envelopeSchema = XmlSchema.Read(stringReader, (sender, args) => { });
                }
            }

            return _envelopeSchema;
        }

        private static void TryValidateEnvelopeDocument(XmlDocument envelopeDocument)
        {
            try
            {
                envelopeDocument.Validate(
                    (sender, args) => LogManager.GetCurrentClassLogger().Error($"Invalid ebMS Envelope Document: {args.Message}"));
            }
            catch (XmlSchemaValidationException exception)
            {
                throw ThrowAS4InvalidEnvelopeException(exception);
            }
        }

        private static AS4Exception ThrowAS4InvalidEnvelopeException(Exception exception)
        {
            return AS4ExceptionBuilder
                .WithDescription("Invalid ebMS Envelope Document")
                .WithInnerException(exception)
                .WithErrorCode(ErrorCode.Ebms0009)
                .WithExceptionType(ExceptionType.InvalidHeader)
                .Build();
        }

        private static XmlDocument LoadXmlDocument(Stream stream)
        {
            stream.Position = 0;

            var document = new XmlDocument {PreserveWhitespace = true};
            document.Load(stream);

            return document;
        }

        private static void DeserializeEnvelope(XmlDocument envelopeDocument, AS4Message as4Message, XmlReader reader)
        {
            // Try to Deserialize the Messaging-Headers first since here an 
            // XmlSerializer is used to perform the deserialization.  
            // XmlSerializer will be positioned on the next node after deserializing, which
            // causes a next Read to position on the next node.
            // By doing this, it could be possible that the Security node is skipped when we
            // try to deserialize the security header first.
            DeserializeMessagingHeader(reader, as4Message);

            DeserializeSecurityHeader(reader, envelopeDocument, as4Message);

            DeserializeBody(reader, as4Message);
        }

        private static void DeserializeSecurityHeader(
            XmlReader reader,
            XmlDocument envelopeDocument,
            AS4Message as4Message)
        {
            if (!IsReadersNameSecurityHeader(reader)) return;

            ISigningStrategy signingStrategy = null;
            IEncryptionStrategy encryptionStrategy = null;

            while (reader.Read() && reader.LocalName.Equals("Security", StringComparison.OrdinalIgnoreCase) == false)
            {
                if (IsReadersNameEncryptedData(reader) && encryptionStrategy == null) encryptionStrategy = EncryptionStrategyBuilder.Create(envelopeDocument).Build();

                if (IsReadersNameSignature(reader)) signingStrategy = new SigningStrategyBuilder(envelopeDocument).Build();
            }

            as4Message.SecurityHeader = new SecurityHeader(signingStrategy, encryptionStrategy);
        }

        private static bool IsReadersNameSignature(XmlReader reader)
            =>
                reader.NodeType == XmlNodeType.Element
                && StringComparer.OrdinalIgnoreCase.Equals(reader.LocalName, "Signature");

        private static bool IsReadersNameEncryptedData(XmlReader reader)
            =>
                reader.NodeType == XmlNodeType.Element
                && StringComparer.OrdinalIgnoreCase.Equals(reader.LocalName, "EncryptedData");

        private static bool IsReadersNameSecurityHeader(XmlReader reader)
            => StringComparer.OrdinalIgnoreCase.Equals(reader.LocalName, "Security") && reader.IsStartElement();

        private static void DeserializeMessagingHeader(XmlReader reader, AS4Message as4Message)
        {
            if (!IsReadersNameMessaging(reader)) return;

            var messagingHeader = AS4XmlSerializer.FromReader<Messaging>(reader);
            as4Message.SignalMessages = GetSignalMessagesFromHeader(messagingHeader);
            as4Message.UserMessages = GetUserMessagesFromHeader(messagingHeader);
            as4Message.SigningId.HeaderSecurityId = messagingHeader.SecurityId;
        }

        private static bool IsReadersNameMessaging(XmlReader reader)
            =>
                StringComparer.OrdinalIgnoreCase.Equals(reader.LocalName, "Messaging") && IsReadersNamespace(reader)
                && reader.IsStartElement();

        private static List<SignalMessage> GetSignalMessagesFromHeader(Messaging messagingHeader)
        {
            if (messagingHeader.SignalMessage == null) return new List<SignalMessage>();

            var messages = new List<SignalMessage>();

            foreach (Xml.SignalMessage signalMessage in messagingHeader.SignalMessage) AddSignalMessageToList(messages, signalMessage);

            return messages;
        }

        private static void AddSignalMessageToList(List<SignalMessage> signalMessages, Xml.SignalMessage signalMessage)
        {
            if (signalMessage.Error != null) signalMessages.Add(AS4Mapper.Map<Error>(signalMessage));

            if (signalMessage.PullRequest != null) signalMessages.Add(AS4Mapper.Map<PullRequest>(signalMessage));

            if (signalMessage.Receipt != null) signalMessages.Add(AS4Mapper.Map<Receipt>(signalMessage));
        }

        private static List<UserMessage> GetUserMessagesFromHeader(Messaging header)
        {
            if (header.UserMessage == null) return new List<UserMessage>();

            IEnumerable<UserMessage> messages = TryMapUserMessages(header);

            return messages.ToList();
        }

        private static IEnumerable<UserMessage> TryMapUserMessages(Messaging header)
        {
            try
            {
                return AS4Mapper.Map<IEnumerable<UserMessage>>(header.UserMessage);
            }
            catch (Exception exception) when (exception.GetBaseException() is AS4Exception)
            {
                throw exception.GetBaseException();
            }
        }

        private static void DeserializeBody(XmlReader reader, AS4Message as4Message)
        {
            if (!IsReadersNameBody(reader)) return;

            var body = AS4XmlSerializer.FromReader<Body>(reader);
            as4Message.SigningId.BodySecurityId = GetBodySecurityId(body);
        }

        private static bool IsReadersNameBody(XmlReader reader)
            =>
                StringComparer.OrdinalIgnoreCase.Equals(reader.LocalName, "Body") && IsReadersNamespace(reader)
                && reader.IsStartElement();

        private static bool IsReadersNamespace(XmlReader reader)
            => reader.NamespaceURI.Equals(Constants.Namespaces.EbmsXmlCore);

        private static string GetBodySecurityId(Body body)
        {
            string securityId = $"body-{Guid.NewGuid()}";
            if (body == null) return securityId;

            IEnumerator enumerator = body.AnyAttr.GetEnumerator();
            while (enumerator.MoveNext())
            {
                var attribute = (XmlAttribute) enumerator.Current;
                if (attribute.LocalName.Equals("Id")) return attribute.Value;
            }

            return securityId;
        }
    }
}
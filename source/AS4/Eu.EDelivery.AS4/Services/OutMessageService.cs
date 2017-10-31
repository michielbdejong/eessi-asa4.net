using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Eu.EDelivery.AS4.Builders.Entities;
using Eu.EDelivery.AS4.Common;
using Eu.EDelivery.AS4.Entities;
using Eu.EDelivery.AS4.Model.Core;
using Eu.EDelivery.AS4.Model.Internal;
using Eu.EDelivery.AS4.Model.PMode;
using Eu.EDelivery.AS4.Repositories;
using MessageExchangePattern = Eu.EDelivery.AS4.Entities.MessageExchangePattern;

namespace Eu.EDelivery.AS4.Services
{
    /// <summary>
    /// Repository to expose Data store related operations
    /// for the Exception Handling Decorator Steps
    /// </summary>
    public class OutMessageService : IOutMessageService
    {
        private readonly IDatastoreRepository _repository;
        private readonly IAS4MessageBodyStore _messageBodyStore;
        private readonly IConfig _configuration;

        /// <summary>
        /// Initializes a new instance of the <see cref="OutMessageService"/> class. 
        /// Create a new Insert Data store Repository
        /// with a given Data store
        /// </summary>
        /// <param name="repository"></param>
        /// <param name="messageBodyStore">The <see cref="IAS4MessageBodyStore"/> that must be used to persist the AS4 Message Body.</param>
        public OutMessageService(IDatastoreRepository repository, IAS4MessageBodyStore messageBodyStore)
            : this(Config.Instance, repository, messageBodyStore) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="OutMessageService" /> class.
        /// </summary>
        /// <param name="config">The configuration.</param>
        /// <param name="respository">The respository.</param>
        /// <param name="messageBodyStore">The as4 message body persister.</param>
        public OutMessageService(IConfig config, IDatastoreRepository respository, IAS4MessageBodyStore messageBodyStore)
        {
            _configuration = config;
            _repository = respository;
            _messageBodyStore = messageBodyStore;
        }

        /// <summary>
        /// Inserts a s4 message.
        /// </summary>
        /// <param name="messagingContext">The messaging context.</param>
        /// <param name="operation">The operation.</param>
        /// <param name="cancellationToken">The cancellation token.</param>
        /// <returns></returns>
        public async Task InsertAS4Message(
            MessagingContext messagingContext,
            Operation operation,
            CancellationToken cancellationToken)
        {
            AS4Message message = messagingContext.AS4Message;
            string messageBodyLocation =
                await _messageBodyStore.SaveAS4MessageAsync(
                    location: _configuration.OutMessageStoreLocation,
                    message: message,
                    cancellation: cancellationToken).ConfigureAwait(false);

            var messageUnits = new List<MessageUnit>();
            messageUnits.AddRange(message.UserMessages);
            messageUnits.AddRange(message.SignalMessages);

            var relatedInMessageMeps =
                _repository.GetInMessagesData(message.SignalMessages.Select(s => s.RefToMessageId).Distinct(), inMsg => new { inMsg.EbmsMessageId, inMsg.MEP })
                           .Distinct()
                           .ToDictionary(r => r.EbmsMessageId, r => MessageExchangePatternUtils.Parse(r.MEP));

            foreach (var messageUnit in messageUnits)
            {
                var sendingPMode = GetSendingPMode(messageUnit is SignalMessage, messagingContext);

                OutMessage outMessage =
                    await CreateOutMessageForMessageUnitAsync(
                            messageUnit: messageUnit,
                            messageContext: messagingContext,
                            sendingPMode: sendingPMode,
                            relatedInMessageMeps: relatedInMessageMeps,
                            location: messageBodyLocation,
                            operation: operation).ConfigureAwait(false);

                _repository.InsertOutMessage(outMessage);
            }
        }

        private static async Task<OutMessage> CreateOutMessageForMessageUnitAsync(
            MessageUnit messageUnit,
            MessagingContext messageContext,
            SendingProcessingMode sendingPMode,
            Dictionary<string, MessageExchangePattern> relatedInMessageMeps,
            string location,
            Operation operation)
        {
            OutMessage outMessage =
                await OutMessageBuilder.ForMessageUnit(messageUnit, messageContext.AS4Message.ContentType, sendingPMode)
                                       .BuildAsync(CancellationToken.None).ConfigureAwait(false);

            outMessage.MessageLocation = location;

            if (outMessage.EbmsMessageType == MessageType.UserMessage.ToString())
            {
                outMessage.SetOperation(operation);
            }
            else
            {
                MessageExchangePattern? inMessageMep = null;

                string refToMessageId = messageUnit.RefToMessageId ?? string.Empty;

                if (relatedInMessageMeps.ContainsKey(refToMessageId))
                {
                    inMessageMep = relatedInMessageMeps[refToMessageId];
                }

                (OutStatus status, Operation operation) replyPattern =
                    DetermineCorrectReplyPattern(messageContext.ReceivingPMode, inMessageMep);

                outMessage.SetStatus(replyPattern.status);
                outMessage.SetOperation(replyPattern.operation);
            }

            return outMessage;
        }

        private SendingProcessingMode GetSendingPMode(bool isSignalMessage, MessagingContext context)
        {
            if (context.SendingPMode?.Id != null)
            {
                return context.SendingPMode;
            }

            ReceivingProcessingMode receivePMode = context.ReceivingPMode;

            if (isSignalMessage && receivePMode != null && receivePMode.ReplyHandling.ReplyPattern == ReplyPattern.Callback)
            {
                return _configuration.GetSendingPMode(receivePMode.ReplyHandling.SendingPMode);
            }

            return null;
        }

        private static (OutStatus, Operation) DetermineCorrectReplyPattern(ReceivingProcessingMode receivingPMode, MessageExchangePattern? inMessageMep)
        {
            if (inMessageMep == null)
            {
                return (OutStatus.Created, Operation.NotApplicable);
            }

            bool isCallback = receivingPMode?.ReplyHandling?.ReplyPattern == ReplyPattern.Callback;
            bool userMessageReceivedViaPulling = inMessageMep == MessageExchangePattern.Pull;

            Operation operation = isCallback || userMessageReceivedViaPulling ? Operation.ToBeSent : Operation.NotApplicable;
            OutStatus status = isCallback || userMessageReceivedViaPulling ? OutStatus.Created : OutStatus.Sent;

            return (status, operation);
        }

        /// <summary>
        /// Updates a <see cref="AS4Message"/>.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="cancellation">The cancellation.</param>
        /// <returns></returns>
        public async Task UpdateAS4MessageToBeSent(AS4Message message, CancellationToken cancellation)
        {
            string ebmsMessageId = message.GetPrimaryMessageId();

            string messageBodyLocation = _repository.GetOutMessageData(ebmsMessageId, m => m.MessageLocation);

            await _messageBodyStore.UpdateAS4MessageAsync(messageBodyLocation, message, cancellation).ConfigureAwait(false);

            _repository.UpdateOutMessage(
                ebmsMessageId,
                m =>
                {
                    m.SetOperation(Operation.ToBeSent);
                    m.MessageLocation = messageBodyLocation;
                });
        }
    }

    public interface IOutMessageService
    {
        /// <summary>
        /// Inserts a s4 message.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="operation">The operation.</param>
        /// <param name="cancellation">The cancellation.</param>
        /// <returns></returns>
        Task InsertAS4Message(MessagingContext message, Operation operation, CancellationToken cancellation);

        /// <summary>
        /// Updates a <see cref="AS4Message"/>.
        /// </summary>
        /// <param name="message">The message.</param>
        /// <param name="cancellation">The cancellation.</param>
        /// <returns></returns>
        Task UpdateAS4MessageToBeSent(AS4Message message, CancellationToken cancellation);
    }
}
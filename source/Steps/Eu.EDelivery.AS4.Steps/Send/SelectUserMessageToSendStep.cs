﻿using System;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Eu.EDelivery.AS4.Common;
using Eu.EDelivery.AS4.Entities;
using Eu.EDelivery.AS4.Exceptions;
using Eu.EDelivery.AS4.Model.Core;
using Eu.EDelivery.AS4.Model.Internal;
using Eu.EDelivery.AS4.Model.PMode;
using Eu.EDelivery.AS4.Repositories;
using Eu.EDelivery.AS4.Serialization;
using NLog;
using MessageExchangePattern = Eu.EDelivery.AS4.Entities.MessageExchangePattern;

namespace Eu.EDelivery.AS4.Steps.Send
{
    /// <summary>
    /// Describes how a MessageUnit should be selected to be sent via Pulling.
    /// </summary>
    /// <seealso cref="IStep" />
    public class SelectUserMessageToSendStep : IStep
    {
        private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();
        private readonly Func<DatastoreContext> _createContext;
        private readonly IAS4MessageBodyStore _messageBodyStore;

        /// <summary>
        /// Initializes a new instance of the <see cref="SelectUserMessageToSendStep"/> class.
        /// </summary>
        public SelectUserMessageToSendStep()
            : this(Registry.Instance.CreateDatastoreContext, Registry.Instance.MessageBodyStore) { }

        /// <summary>
        /// Initializes a new instance of the <see cref="SelectUserMessageToSendStep" /> class.
        /// </summary>
        /// <param name="createContext">The create context.</param>
        /// <param name="messageBodyStore">The message body store.</param>
        public SelectUserMessageToSendStep(Func<DatastoreContext> createContext, IAS4MessageBodyStore messageBodyStore)
        {
            _createContext = createContext;
            _messageBodyStore = messageBodyStore;
        }

        /// <summary>
        /// Execute the step for a given <paramref name="messagingContext" />.
        /// </summary>
        /// <param name="messagingContext">Message used during the step execution.</param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<StepResult> ExecuteAsync(
            MessagingContext messagingContext,
            CancellationToken cancellationToken)
        {
            var pullRequest = messagingContext.AS4Message.PrimarySignalMessage as PullRequest;

            if (pullRequest == null)
            {
                throw new InvalidMessageException("The received message is not a PullRequest-message.");
            }

            (bool hasMatch, OutMessage match) selection = RetrieveUserMessageForPullRequest(pullRequest);

            if (selection.hasMatch)
            {
                Logger.Info($"User Message found for Pull Request: '{messagingContext.AS4Message.GetPrimaryMessageId()}'");

                // Retrieve the existing MessageBody and put that stream in the MessagingContext.
                // The HttpReceiver processor will make sure that it gets serialized to the http response stream.

                var messageBody = await selection.match.RetrieveMessageBody(_messageBodyStore);

                messagingContext.ModifyContext(new ReceivedMessage(messageBody, selection.match.ContentType), MessagingContextMode.Send);

                messagingContext.SendingPMode = AS4XmlSerializer.FromString<SendingProcessingMode>(selection.match.PMode);

                return StepResult.Success(messagingContext);
            }

            Logger.Warn($"No User Message found for Pull Request: '{messagingContext.AS4Message.GetPrimaryMessageId()}'");

            AS4Message pullRequestWarning = AS4Message.Create(new PullRequestError());
            messagingContext.ModifyContext(pullRequestWarning);

            return StepResult.Success(messagingContext).AndStopExecution();
        }

        private (bool, OutMessage) RetrieveUserMessageForPullRequest(PullRequest pullRequestMessage)
        {
            var options = new TransactionOptions { IsolationLevel = IsolationLevel.RepeatableRead };

            using (var scope = new TransactionScope(TransactionScopeOption.Required, options))
            {
                using (DatastoreContext context = _createContext())
                {
                    var repository = new DatastoreRepository(context);

                    OutMessage message =
                        repository.GetOutMessageData(
                            m => PullRequestQuery(m, pullRequestMessage),
                            m => m);

                    if (message == null)
                    {
                        return (false, null);
                    }

                    repository.UpdateOutMessage(message.EbmsMessageId, m => m.Operation = Operation.Sent);
                    context.SaveChanges();
                    scope.Complete();

                    return (true, message);
                }
            }
        }

        private static bool PullRequestQuery(MessageEntity userMessage, PullRequest pullRequest)
        {
            return userMessage.Mpc == pullRequest.Mpc
                   && userMessage.Operation == Operation.ToBeSent
                   && userMessage.MEP == MessageExchangePattern.Pull;
        }
    }
}
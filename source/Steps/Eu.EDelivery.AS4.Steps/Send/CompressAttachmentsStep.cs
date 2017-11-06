﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Eu.EDelivery.AS4.Model.Core;
using Eu.EDelivery.AS4.Model.Internal;
using Eu.EDelivery.AS4.Streaming;
using NLog;

namespace Eu.EDelivery.AS4.Steps.Send
{
    /// <summary>
    /// Describes how the attachments of an AS4 message must be compressed.
    /// </summary>
    [Description("This step compresses the attachments of an AS4 Message if compression is enabled in the sending PMode.")]
    [Info("Compress attachments")]
    public class CompressAttachmentsStep : IStep
    {
        private static readonly ILogger Logger = LogManager.GetCurrentClassLogger();
        private MessagingContext _messagingContext;

        /// <summary>
        /// Compress the <see cref="AS4Message" /> if required
        /// </summary>
        /// <param name="messagingContext"></param>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        public async Task<StepResult> ExecuteAsync(MessagingContext messagingContext, CancellationToken cancellationToken)
        {
            if (!messagingContext.SendingPMode.MessagePackaging.UseAS4Compression)
            {
                return ReturnSameMessagingContext(messagingContext);
            }

            _messagingContext = messagingContext;
            await TryCompressAS4MessageAsync(messagingContext.AS4Message.Attachments).ConfigureAwait(false);

            return StepResult.Success(messagingContext);
        }

        private static StepResult ReturnSameMessagingContext(MessagingContext messagingContext)
        {
            Logger.Debug($"Sending PMode {messagingContext.SendingPMode.Id} Compression is disabled");
            return StepResult.Success(messagingContext);
        }

        private async Task TryCompressAS4MessageAsync(IEnumerable<Attachment> attachments)
        {
            try
            {
                Logger.Info(
                    $"{_messagingContext.EbmsMessageId} Compress AS4 Message Attachments with GZip Compression");
                await CompressAttachments(attachments).ConfigureAwait(false);
            }
            catch (SystemException exception)
            {
                throw ThrowAS4CompressingException(exception);
            }
        }

        private static async Task CompressAttachments(IEnumerable<Attachment> attachments)
        {
            foreach (Attachment attachment in attachments)
            {
                await CompressAttachmentAsync(attachment).ConfigureAwait(false);
                AssignAttachmentProperties(attachment);
            }
        }

        private static async Task CompressAttachmentAsync(Attachment attachment)
        {
            VirtualStream outputStream =
                VirtualStream.CreateVirtualStream(
                    attachment.Content.CanSeek ? attachment.Content.Length : VirtualStream.ThresholdMax);

            var compressionLevel = DetermineCompressionLevelFor(attachment);

            using (var gzipCompression = new GZipStream(outputStream, compressionLevel: compressionLevel, leaveOpen: true))
            {
                await attachment.Content.CopyToAsync(gzipCompression).ConfigureAwait(false);
            }

            outputStream.Position = 0;
            attachment.Content = outputStream;
        }

        private static CompressionLevel DetermineCompressionLevelFor(Attachment attachment)
        {
            if (attachment.ContentType.Equals("application/gzip", StringComparison.OrdinalIgnoreCase))
            {
                // In certain cases, we do not want to waste time compressing the attachment, since
                // compressing will only take time without noteably decreasing the attachment size.
                return CompressionLevel.NoCompression;
            }

            if (attachment.Content.CanSeek)
            {
                const long twelveKilobytes = 12_288;
                const long hundredMegabytes = 104_857_600;

                if (attachment.Content.Length <= twelveKilobytes)
                {
                    return CompressionLevel.NoCompression;
                }

                if (attachment.Content.Length <= hundredMegabytes)
                {
                    return CompressionLevel.Fastest;
                }
            }

            return CompressionLevel.Optimal;
        }

        private static void AssignAttachmentProperties(Attachment attachment)
        {
            attachment.Properties["CompressionType"] = "application/gzip";
            attachment.Properties["MimeType"] = attachment.ContentType;
            attachment.ContentType = "application/gzip";
        }

        private static Exception ThrowAS4CompressingException(Exception innerException)
        {
            const string description = "Attachments cannot be compressed";
            Logger.Error(description);

            return new InvalidDataException(description, innerException);
        }
    }
}
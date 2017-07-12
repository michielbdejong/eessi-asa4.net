﻿using System;
using System.ComponentModel;
using System.Security.Cryptography.X509Certificates;
using System.Xml.Serialization;
using Newtonsoft.Json;

namespace Eu.EDelivery.AS4.Model.PMode
{
    /// <summary>
    /// Receiving PMode configuration
    /// </summary>
    [XmlType(Namespace = "eu:edelivery:as4:pmode")]
    [XmlRoot("PMode", Namespace = "eu:edelivery:as4:pmode", IsNullable = false)]
    public class ReceivingProcessingMode : IPMode
    {
        public string Id { get; set; }
        public MessageExchangePattern Mep { get; set; }
        public MessageExchangePatternBinding MepBinding { get; set; }        
        public ReceiveReliability Reliability { get; set; }
        public ReplyHandlingSetting ReplyHandling { get; set; }
        public Receivehandling ExceptionHandling { get; set; }

        [XmlElement(ElementName = "Security")] public ReceiveSecurity Security { get; set; }
        public MessagePackaging MessagePackaging { get; set; }
        public Deliver Deliver { get; set; }

        public ReceivingProcessingMode()
        {            
            Reliability = new ReceiveReliability();
            ReplyHandling = new ReplyHandlingSetting();
            ExceptionHandling = new Receivehandling();
            Security = new ReceiveSecurity();
            MessagePackaging = new MessagePackaging();
            Deliver = new Deliver();
        }        
    }    

    public class ReceiveReliability
    {
        public DuplicateElimination DuplicateElimination { get; set; }

        public ReceiveReliability()
        {
            DuplicateElimination = new DuplicateElimination();
        }
    }

    public class DuplicateElimination
    {
        [Description("Do not allow duplicate messages")]
        public bool IsEnabled { get; set; }

        public DuplicateElimination()
        {
            IsEnabled = false;
        }
    }

    public class ReplyHandlingSetting
    {
        public ReplyPattern ReplyPattern { get; set; }
        public string SendingPMode { get; set; }
        public ReceiveReceiptHandling ReceiptHandling { get; set; }
        public ReceiveErrorHandling ErrorHandling { get; set; }

        /// <summary>
        /// Initializes a new instance of the <see cref="ReplyHandlingSetting"/> class.
        /// </summary>
        public ReplyHandlingSetting()
        {
            ReplyPattern = ReplyPattern.Response;
            ReceiptHandling = new ReceiveReceiptHandling();
            ErrorHandling = new ReceiveErrorHandling();
        }
    }

    public class ReceiveReceiptHandling
    {
        public bool UseNNRFormat { get; set; }

        public ReceiveReceiptHandling()
        {
            UseNNRFormat = false;
        }
    }

    public class Receivehandling
    {
        public bool NotifyMessageConsumer { get; set; }
        public Method NotifyMethod { get; set; }

        public Receivehandling()
        {
            NotifyMessageConsumer = false;
            NotifyMethod = new Method();
        }
    }

    public class ReceiveErrorHandling
    {
        public bool UseSoapFault { get; set; }
        public int ResponseHttpCode { get; set; }
      

        public ReceiveErrorHandling()
        {
            UseSoapFault = false;
        }
    }

    public class ReceiveSecurity
    {
        public SigningVerification SigningVerification { get; set; }
        public Decryption Decryption { get; set; }

        public ReceiveSecurity()
        {
            SigningVerification = new SigningVerification();
            Decryption = new Decryption();
        }
    }

    public class SigningVerification
    {
        public Limit Signature { get; set; }

        public SigningVerification()
        {
            Signature = Limit.Allowed;
        }
    }

    public class Decryption
    {
        public Limit Encryption { get; set; }
        public X509FindType PrivateKeyFindType { get; set; }
        public string PrivateKeyFindValue { get; set; }

        #region Serialization management

        [XmlIgnore]
        [JsonIgnore]
        public bool PrivateKeyFindTypeSpecified { get; set; }

        [XmlIgnore]
        [JsonIgnore]
        public bool PrivateKeyFindValueSpecified => !String.IsNullOrWhiteSpace(PrivateKeyFindValue);

        #endregion

        public Decryption()
        {
            Encryption = Limit.Allowed;
        }
    }

    public class Deliver
    {
        public bool IsEnabled { get; set; }
        public Method PayloadReferenceMethod { get; set; }
        public Method DeliverMethod { get; set; }

        public Deliver()
        {
            IsEnabled = false;
            PayloadReferenceMethod = new Method();
            DeliverMethod = new Method();
        }
    }

    public enum ReplyPattern
    {
        Response = 0,
        Callback
    }

    public enum Limit
    {
        Allowed = 0,
        NotAllowed,
        Required,
        Ignored
    }
}
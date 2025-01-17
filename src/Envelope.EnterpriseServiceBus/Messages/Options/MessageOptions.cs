﻿using Envelope.ServiceBus.ErrorHandling;
using Envelope.Transactions;
using Envelope.Validation;
using System.Text;

namespace Envelope.EnterpriseServiceBus.Messages.Options;

public class MessageOptions : IMessageOptions, IValidable
{
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
	/// <inheritdoc/>
	public ITransactionController TransactionController { get; set; }

	/// <inheritdoc/>
	public string ExchangeName { get; set; }

	/// <inheritdoc/>
	public string ContentType { get; set; }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

	/// <inheritdoc/>
	public Encoding? ContentEncoding { get; set; }

	/// <inheritdoc/>
	public bool DisabledMessagePersistence { get; set; }

	/// <inheritdoc/>
	public Guid? IdSession { get; set; }

	/// <inheritdoc/>
	public string? RoutingKey { get; set; }

	public bool IsAsynchronousInvocation { get; set; }

	/// <inheritdoc/>
	public IErrorHandlingController? ErrorHandling { get; set; }

	public IMessageHeaders? Headers { get; set; }

	/// <inheritdoc/>
	public TimeSpan? Timeout { get; set; }

	/// <inheritdoc/>
	public bool IsCompressContent { get; set; }

	/// <inheritdoc/>
	public bool IsEncryptContent { get; set; }

	/// <inheritdoc/>
	public int Priority { get; set; }

	/// <inheritdoc/>
	public bool DisableFaultQueue { get; set; }

	public bool? ThrowNoHandlerException { get; set; }

	public List<IValidationMessage>? Validate(
		string? propertyPrefix = null,
		ValidationBuilder? validationBuilder = null,
		Dictionary<string, object>? globalValidationContext = null,
		Dictionary<string, object>? customValidationContext = null)
	{
		validationBuilder ??= new ValidationBuilder();
		validationBuilder.SetValidationMessages(propertyPrefix, globalValidationContext)
			.IfNullOrWhiteSpace(ExchangeName)
			.IfNullOrWhiteSpace(ContentType)
			;

		return validationBuilder.Build();
	}
}

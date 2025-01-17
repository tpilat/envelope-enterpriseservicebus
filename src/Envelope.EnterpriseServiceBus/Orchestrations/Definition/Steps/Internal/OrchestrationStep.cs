﻿using Envelope.Converters;
using Envelope.EnterpriseServiceBus.Orchestrations.Definition.Steps.Body;
using Envelope.ServiceBus.ErrorHandling;
using Envelope.Validation;

namespace Envelope.EnterpriseServiceBus.Orchestrations.Definition.Steps.Internal;

internal abstract class OrchestrationStep : IOrchestrationStep, IValidable
{
	public virtual Guid IdStep { get; private set; }

	public abstract Type? BodyType { get; }

	private string _name;
	public virtual string Name
	{
		get => _name;
		set
		{
			_name = value;
			IdStep = GuidConverter.ToGuid(_name);
		}
	}

	public bool IsRootStep { get; set; }

	public IOrchestrationDefinition OrchestrationDefinition { get; set; }

	public virtual IOrchestrationStep? NextStep { get; set; }

	public Dictionary<object, IOrchestrationStep> Branches { get; }
	IReadOnlyDictionary<object, IOrchestrationStep> IOrchestrationStep.Branches => Branches;

	public IOrchestrationStep? BranchController { get; set; }

	public IOrchestrationStep? StartingStep { get; set; }

	public bool IsStartingStep => StartingStep == this;

	public AssignParameters? SetInputParametersInternal { get; set; }

	public AssignParameters? SetOutputParametersInternal { get; set; }
	public virtual IErrorHandlingController? ErrorHandlingController { get; set; }

	public TimeSpan? DistributedLockExpiration { get; set; }

#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
	
	public OrchestrationStep(string name)
	{
		Name = !string.IsNullOrWhiteSpace(name)
			? name
			: throw new ArgumentNullException(nameof(name));

		IdStep = GuidConverter.ToGuid(name);
		Branches = new();
	}

#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.

	public abstract IStepBody? ConstructBody(IServiceProvider serviceProvider);

	public IErrorHandlingController? GetErrorHandlingController()
		=> ErrorHandlingController ?? OrchestrationDefinition.DefaultErrorHandling;

	public bool CanRetry(int retryCount)
	{
		var errorHandlingController = GetErrorHandlingController();
		if (errorHandlingController == null)
			return false;

		return errorHandlingController.CanRetry(retryCount);
	}

	public TimeSpan? GetRetryInterval(int retryCount)
	{
		var errorHandlingController = GetErrorHandlingController();
		if (errorHandlingController == null)
			return null;

		return errorHandlingController.GetRetryTimeSpan(retryCount);
	}

	public List<IValidationMessage>? Validate(
		string? propertyPrefix = null,
		ValidationBuilder? validationBuilder = null,
		Dictionary<string, object>? globalValidationContext = null,
		Dictionary<string, object>? customValidationContext = null)
	{
		validationBuilder ??= new ValidationBuilder();
		return validationBuilder.Build();
	}

	public override string ToString()
		=> $"{Name} - {BodyType?.Name} - {IdStep}";
}

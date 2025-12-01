variable "region" {
  description = "AWS region for all control-plane resources"
  type        = string
}

variable "parameter_prefix" {
  description = "SSM parameter path prefix for ring parameters"
  type        = string
  default     = "/release-rings/amis"
}

variable "rings" {
  description = "Ordered list of release ring names"
  type        = list(string)
  default     = ["nightly", "edge", "beta", "stable", "lts"]
}

variable "table_name" {
  description = "DynamoDB table name for control-plane state"
  type        = string
  default     = "ReleaseRingState"
}

variable "function_name" {
  description = "Lambda function name for the reconciliation worker"
  type        = string
  default     = "release-ring-service-worker"
}

variable "function_runtime" {
  description = "Lambda runtime identifier"
  type        = string
  default     = "dotnet10"
}

variable "function_handler" {
  description = "Lambda handler in Assembly::Namespace.Class::Method format"
  type        = string
  default     = "ReleaseRingService.Worker::ReleaseRingService.Worker.Function::HandleAsync"
}

variable "function_memory_size" {
  description = "Lambda memory in MB"
  type        = number
  default     = 256
}

variable "function_timeout" {
  description = "Lambda timeout in seconds"
  type        = number
  default     = 30
}

variable "function_architecture" {
  description = "Lambda CPU architecture"
  type        = string
  default     = "arm64"
}

variable "schedule_expression" {
  description = "EventBridge Scheduler expression for reconciliation"
  type        = string
  default     = "rate(5 minutes)"
}

variable "lambda_source_dir" {
  description = "Path to the published Lambda function output"
  type        = string
}

variable "lambda_zip_output" {
  description = "Path where the Lambda deployment zip will be written"
  type        = string
  default     = "lambda_package.zip"
}

variable "log_retention_days" {
  description = "CloudWatch log retention in days"
  type        = number
  default     = 7
}

variable "tags" {
  description = "Tags applied to all taggable resources"
  type        = map(string)
  default = {
    Service   = "release-ring-service"
    ManagedBy = "Terraform"
  }
}

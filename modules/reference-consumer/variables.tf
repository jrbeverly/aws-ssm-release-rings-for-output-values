variable "vpc_id" {
  description = "VPC ID where the reference consumer is deployed"
  type        = string
}

variable "subnet_ids" {
  description = "Subnet IDs for the Auto Scaling Group (must span at least one AZ in the target region)"
  type        = list(string)
}

variable "parameter_prefix" {
  description = "SSM parameter path prefix matching the control-plane module"
  type        = string
  default     = "/release-rings/amis"
}

variable "ring" {
  description = "Release ring the consumer follows (nightly, edge, beta, stable, lts)"
  type        = string
  default     = "stable"

  validation {
    condition     = contains(["nightly", "edge", "beta", "stable", "lts"], var.ring)
    error_message = "Ring must be one of: nightly, edge, beta, stable, lts."
  }
}

variable "instance_type" {
  description = "EC2 instance type for the reference consumer"
  type        = string
  default     = "t3.micro"
}

variable "desired_capacity" {
  description = "Desired number of instances in the Auto Scaling Group"
  type        = number
  default     = 1
}

variable "min_size" {
  description = "Minimum number of instances in the Auto Scaling Group"
  type        = number
  default     = 1
}

variable "max_size" {
  description = "Maximum number of instances in the Auto Scaling Group"
  type        = number
  default     = 1
}

variable "tags" {
  description = "Tags applied to all taggable resources"
  type        = map(string)
  default = {
    Service   = "release-ring-reference-consumer"
    ManagedBy = "Terraform"
    Purpose   = "reference-integration"
  }
}

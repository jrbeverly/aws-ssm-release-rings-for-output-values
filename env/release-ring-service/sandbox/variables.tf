variable "region" {
  type    = string
  default = "us-west-2"
}

variable "parameter_prefix" {
  type    = string
  default = "/release-rings/amis"
}

variable "lambda_source_dir" {
  type    = string
  default = "../../../src/ReleaseRingService/ReleaseRingService.Worker/bin/Release/net10.0/publish"
}

variable "schedule_expression" {
  type    = string
  default = "rate(1 minute)"
}

variable "consumer_ring" {
  type    = string
  default = "beta"
}

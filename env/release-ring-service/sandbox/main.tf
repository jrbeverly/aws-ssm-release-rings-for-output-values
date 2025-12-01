provider "aws" {
  region = var.region
}

module "control_plane" {
  source = "../../../modules/release-ring-control-plane"

  region           = var.region
  parameter_prefix = var.parameter_prefix

  lambda_source_dir   = var.lambda_source_dir
  schedule_expression = var.schedule_expression
}

resource "aws_vpc" "consumer" {
  cidr_block = "10.42.0.0/24"
  tags       = { Name = "aws-ssm-release-rings-for-output-values" }
}

resource "aws_subnet" "consumer" {
  vpc_id     = aws_vpc.consumer.id
  cidr_block = "10.42.0.0/26"
  tags       = { Name = "aws-ssm-release-rings-for-output-values" }
}

module "consumer" {
  source = "../../../modules/reference-consumer"

  vpc_id           = aws_vpc.consumer.id
  subnet_ids       = [aws_subnet.consumer.id]
  parameter_prefix = var.parameter_prefix
  ring             = var.consumer_ring
  desired_capacity = 0
  min_size         = 0

  depends_on = [module.control_plane]
}

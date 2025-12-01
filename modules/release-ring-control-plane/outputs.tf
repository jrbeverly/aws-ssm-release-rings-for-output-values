output "dynamodb_table_arn" {
  description = "ARN of the DynamoDB control-plane table"
  value       = aws_dynamodb_table.main.arn
}

output "dynamodb_table_name" {
  description = "Name of the DynamoDB control-plane table"
  value       = aws_dynamodb_table.main.name
}

output "lambda_function_arn" {
  description = "ARN of the reconciliation worker Lambda"
  value       = aws_lambda_function.worker.arn
}

output "lambda_function_name" {
  description = "Function name of the reconciliation worker Lambda"
  value       = aws_lambda_function.worker.function_name
}

output "lambda_role_arn" {
  description = "ARN of the Lambda execution IAM role"
  value       = aws_iam_role.lambda_exec.arn
}

output "lambda_role_name" {
  description = "Name of the Lambda execution IAM role"
  value       = aws_iam_role.lambda_exec.name
}

output "ssm_parameter_names" {
  description = "Full SSM parameter names for all release rings"
  value = [
    for ring in var.rings :
    aws_ssm_parameter.ring[ring].name
  ]
}

output "ssm_parameter_arns" {
  description = "ARNs of all managed SSM ring parameters"
  value = {
    for ring in var.rings :
    ring => aws_ssm_parameter.ring[ring].arn
  }
}

output "scheduler_role_arn" {
  description = "ARN of the EventBridge Scheduler IAM role"
  value       = aws_iam_role.scheduler.arn
}

output "cloudwatch_log_group_name" {
  description = "CloudWatch log group for the Lambda function"
  value       = aws_cloudwatch_log_group.lambda.name
}

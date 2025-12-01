output "dynamodb_table_name" {
  value = module.control_plane.dynamodb_table_name
}

output "lambda_function_name" {
  value = module.control_plane.lambda_function_name
}

output "ssm_parameter_names" {
  value = module.control_plane.ssm_parameter_names
}

output "autoscaling_group_name" {
  value = module.consumer.autoscaling_group_name
}

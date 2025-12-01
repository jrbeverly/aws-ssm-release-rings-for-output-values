output "launch_template_id" {
  description = "ID of the reference launch template"
  value       = aws_launch_template.consumer.id
}

output "launch_template_latest_version" {
  description = "Latest version number of the reference launch template"
  value       = aws_launch_template.consumer.latest_version
}

output "launch_template_image_id" {
  description = "ImageId of the launch template (a resolve:ssm: reference, not a fixed AMI ID)"
  value       = "resolve:ssm:${var.parameter_prefix}/${var.ring}"
}

output "autoscaling_group_name" {
  description = "Name of the reference Auto Scaling Group"
  value       = aws_autoscaling_group.consumer.name
}

output "autoscaling_group_arn" {
  description = "ARN of the reference Auto Scaling Group"
  value       = aws_autoscaling_group.consumer.arn
}

output "security_group_id" {
  description = "ID of the reference consumer security group"
  value       = aws_security_group.consumer.id
}

output "instance_profile_name" {
  description = "Name of the instance profile attached to launched instances"
  value       = aws_iam_instance_profile.consumer.name
}

output "current_ssm_parameter_value" {
  description = "Current value of the followed ring parameter (resolved at read time; launch-time value may differ)"
  value       = data.aws_ssm_parameter.ring.value
}

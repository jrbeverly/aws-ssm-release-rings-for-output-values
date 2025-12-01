terraform {
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = ">= 5.0"
    }
  }
}

# ── SSM parameter reference (read-only) ────────────────────────────────────
#
# The launch template resolves this parameter at instance launch time.
# The parameter itself is owned and written exclusively by the control
# plane. This data source is informational only; the launch template
# uses a resolve:ssm: reference so EC2 resolves the value dynamically.

data "aws_ssm_parameter" "ring" {
  name = "${var.parameter_prefix}/${var.ring}"
}

# ── Security group ─────────────────────────────────────────────────────────

resource "aws_security_group" "consumer" {
  name_prefix = "release-ring-ref-"
  description = "Reference consumer security group"
  vpc_id      = var.vpc_id

  ingress {
    description = "Allow HTTP from within the VPC for validation"
    from_port   = 80
    to_port     = 80
    protocol    = "tcp"
    cidr_blocks = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"]
  }

  egress {
    description = "Allow all outbound traffic"
    from_port   = 0
    to_port     = 0
    protocol    = "-1"
    cidr_blocks = ["0.0.0.0/0"]
  }

  tags = var.tags

  lifecycle {
    create_before_destroy = true
  }
}

# ── IAM instance profile ──────────────────────────────────────────────────

resource "aws_iam_role" "instance" {
  name_prefix = "release-ring-ref-instance-"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect = "Allow"
        Principal = {
          Service = "ec2.amazonaws.com"
        }
        Action = "sts:AssumeRole"
      }
    ]
  })

  tags = var.tags
}

resource "aws_iam_role_policy_attachment" "ssm_core" {
  role       = aws_iam_role.instance.name
  policy_arn = "arn:aws:iam::aws:policy/AmazonSSMManagedInstanceCore"
}

resource "aws_iam_instance_profile" "consumer" {
  name_prefix = "release-ring-ref-"
  role        = aws_iam_role.instance.name
}

# ── Launch template ────────────────────────────────────────────────────────
#
# IMAGE_ID IS A DYNAMIC SSM REFERENCE, NOT A FIXED AMI ID.
#
# EC2 resolves resolve:ssm:<parameter-name> at instance launch time.
# Instance launches pick up the current ring value without requiring
# a launch-template version update. Ring promotions take effect for
# future launches automatically.

resource "aws_launch_template" "consumer" {
  name_prefix = "release-ring-ref-"

  # The central design decision: ImageId uses resolve:ssm: indirection
  # so that EC2 resolves the current ring parameter value at launch time.
  image_id = "resolve:ssm:${var.parameter_prefix}/${var.ring}"

  instance_type = var.instance_type

  iam_instance_profile {
    name = aws_iam_instance_profile.consumer.name
  }

  vpc_security_group_ids = [aws_security_group.consumer.id]

  metadata_options {
    http_endpoint = "enabled"
    http_tokens   = "required"
  }

  tag_specifications {
    resource_type = "instance"
    tags = merge(var.tags, {
      Name = "release-ring-ref-consumer"
    })
  }

  tags = var.tags

  lifecycle {
    create_before_destroy = true
  }
}

# ── Auto Scaling Group ─────────────────────────────────────────────────────
#
# The ASG uses the launch template above. When the ASG launches a new
# instance (scale-out, instance replacement, or AZ rebalance), EC2
# resolves the current SSM parameter value for the configured ring.
# Existing instances are not refreshed when the ring parameter changes.

resource "aws_autoscaling_group" "consumer" {
  name_prefix = "release-ring-ref-"

  vpc_zone_identifier = var.subnet_ids

  min_size         = var.min_size
  max_size         = var.max_size
  desired_capacity = var.desired_capacity

  health_check_type         = "EC2"
  health_check_grace_period = 300

  launch_template {
    id      = aws_launch_template.consumer.id
    version = "$Latest"
  }

  tag {
    key                 = "Name"
    value               = "release-ring-ref-consumer"
    propagate_at_launch = true
  }

  tag {
    key                 = "ReleaseRing"
    value               = var.ring
    propagate_at_launch = true
  }

  lifecycle {
    create_before_destroy = true
  }
}

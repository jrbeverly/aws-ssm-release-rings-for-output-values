terraform {
  required_providers {
    aws = {
      source  = "hashicorp/aws"
      version = ">= 5.0"
    }
    archive = {
      source  = "hashicorp/archive"
      version = ">= 2.0"
    }
  }
}

# ── Lambda deployment package ───────────────────────────────────────────────

data "archive_file" "lambda" {
  type        = "zip"
  source_dir  = var.lambda_source_dir
  output_path = "${path.module}/${var.lambda_zip_output}"
}

# ── DynamoDB table (system of record) ───────────────────────────────────────

resource "aws_dynamodb_table" "main" {
  name         = var.table_name
  billing_mode = "PAY_PER_REQUEST"

  hash_key  = "PK"
  range_key = "SK"

  attribute {
    name = "PK"
    type = "S"
  }
  attribute {
    name = "SK"
    type = "S"
  }
  attribute {
    name = "GSI1PK"
    type = "S"
  }
  attribute {
    name = "GSI1SK"
    type = "S"
  }

  global_secondary_index {
    name            = "GSI1"
    hash_key        = "GSI1PK"
    range_key       = "GSI1SK"
    projection_type = "ALL"
  }

  tags = var.tags
}

# ── SSM ring parameters (publication surface) ───────────────────────────────

# aws:ec2:image parameters are validated asynchronously; a nonexistent AMI
# placeholder is silently never created, so seed with a real public AMI.
data "aws_ssm_parameter" "seed_ami" {
  name = "/aws/service/ami-amazon-linux-latest/al2023-ami-kernel-default-x86_64"
}

resource "aws_ssm_parameter" "ring" {
  for_each = toset(var.rings)

  name        = "${var.parameter_prefix}/${each.key}"
  type        = "String"
  data_type   = "aws:ec2:image"
  value       = data.aws_ssm_parameter.seed_ami.insecure_value
  description = "Release ring parameter for ${each.key} — managed by the release-ring control plane"

  tags = var.tags

  lifecycle {
    ignore_changes = [value]
  }
}

# ── IAM: Lambda execution role ──────────────────────────────────────────────

resource "aws_iam_role" "lambda_exec" {
  name = "${var.function_name}-exec-role"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect = "Allow"
        Principal = {
          Service = "lambda.amazonaws.com"
        }
        Action = "sts:AssumeRole"
      }
    ]
  })

  tags = var.tags
}

# CloudWatch Logs (via managed policy)
resource "aws_iam_role_policy_attachment" "lambda_basic_exec" {
  role       = aws_iam_role.lambda_exec.name
  policy_arn = "arn:aws:iam::aws:policy/service-role/AWSLambdaBasicExecutionRole"
}

# DynamoDB access: full read/write on the control-plane table
resource "aws_iam_role_policy" "lambda_dynamodb" {
  name = "${var.function_name}-dynamodb"
  role = aws_iam_role.lambda_exec.name

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect = "Allow"
        Action = [
          "dynamodb:GetItem",
          "dynamodb:BatchGetItem",
          "dynamodb:PutItem",
          "dynamodb:UpdateItem",
          "dynamodb:DeleteItem",
          "dynamodb:Query",
          "dynamodb:Scan"
        ]
        Resource = [
          aws_dynamodb_table.main.arn,
          "${aws_dynamodb_table.main.arn}/index/GSI1"
        ]
      }
    ]
  })
}

# SSM access: read and write on managed ring parameters
resource "aws_iam_role_policy" "lambda_ssm" {
  name = "${var.function_name}-ssm"
  role = aws_iam_role.lambda_exec.name

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect = "Allow"
        Action = [
          "ssm:GetParameter",
          "ssm:GetParameters",
          "ssm:PutParameter"
        ]
        Resource = [
          for ring in var.rings :
          "arn:aws:ssm:${var.region}:*:parameter${var.parameter_prefix}/${ring}"
        ]
      }
    ]
  })
}

# EC2 access: validate candidate AMIs
resource "aws_iam_role_policy" "lambda_ec2" {
  name = "${var.function_name}-ec2"
  role = aws_iam_role.lambda_exec.name

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect   = "Allow"
        Action   = ["ec2:DescribeImages"]
        Resource = "*"
      }
    ]
  })
}

# ── CloudWatch log group ────────────────────────────────────────────────────

resource "aws_cloudwatch_log_group" "lambda" {
  name              = "/aws/lambda/${var.function_name}"
  retention_in_days = var.log_retention_days

  tags = var.tags
}

# ── Lambda function ─────────────────────────────────────────────────────────

resource "aws_lambda_function" "worker" {
  function_name = var.function_name
  role          = aws_iam_role.lambda_exec.arn

  filename         = data.archive_file.lambda.output_path
  source_code_hash = data.archive_file.lambda.output_base64sha256

  runtime       = var.function_runtime
  handler       = var.function_handler
  memory_size   = var.function_memory_size
  timeout       = var.function_timeout
  architectures = [var.function_architecture]

  depends_on = [
    aws_iam_role_policy_attachment.lambda_basic_exec,
    aws_cloudwatch_log_group.lambda
  ]

  tags = var.tags
}

# ── IAM: EventBridge Scheduler role ─────────────────────────────────────────

resource "aws_iam_role" "scheduler" {
  name = "${var.function_name}-scheduler-role"

  assume_role_policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect = "Allow"
        Principal = {
          Service = "scheduler.amazonaws.com"
        }
        Action = "sts:AssumeRole"
      }
    ]
  })

  tags = var.tags
}

resource "aws_iam_role_policy" "scheduler_invoke_lambda" {
  name = "${var.function_name}-scheduler-invoke"
  role = aws_iam_role.scheduler.name

  policy = jsonencode({
    Version = "2012-10-17"
    Statement = [
      {
        Effect   = "Allow"
        Action   = ["lambda:InvokeFunction"]
        Resource = [aws_lambda_function.worker.arn]
      }
    ]
  })
}

# ── EventBridge Scheduler ───────────────────────────────────────────────────

resource "aws_scheduler_schedule" "reconcile" {
  name       = "${var.function_name}-reconcile"
  group_name = "default"

  flexible_time_window {
    mode = "OFF"
  }

  schedule_expression = var.schedule_expression

  target {
    arn      = aws_lambda_function.worker.arn
    role_arn = aws_iam_role.scheduler.arn
  }
}

# ── Lambda resource policy: allow Scheduler to invoke ───────────────────────

resource "aws_lambda_permission" "allow_scheduler" {
  statement_id  = "AllowEventBridgeSchedulerInvoke"
  action        = "lambda:InvokeFunction"
  function_name = aws_lambda_function.worker.function_name
  principal     = "scheduler.amazonaws.com"
  source_arn    = aws_scheduler_schedule.reconcile.arn
}

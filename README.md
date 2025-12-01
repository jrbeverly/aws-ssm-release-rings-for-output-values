# AWS SSM Release Rings for Output Values

> [!WARNING]
> **AI-authored:** This change was autonomously planned and implemented by an AI software factory from a human-authored specification, with possible subsequent human review or modification.

Tests whether SSM parameters of type `aws:ec2:image` can act as release rings (nightly → edge → beta → stable → lts): a .NET Lambda promotes registered AMIs through the rings on soak timers, and an Auto Scaling Group whose launch template uses `resolve:ssm:` picks up whatever the ring holds at launch time.

```sh
make publish-worker
cd env/release-ring-service/sandbox && terraform init && terraform apply

# there is no bootstrap command; the system config row is written directly
aws dynamodb put-item --table-name ReleaseRingState --item '{
  "PK":{"S":"SYSTEM"},"SK":{"S":"CONFIG"},"EntityType":{"N":"1"},"Version":{"N":"1"},
  "Region":{"S":"us-west-2"},"ParameterPrefix":{"S":"/release-rings/amis"},
  "Rings":{"SS":["nightly","edge","beta","stable","lts"]},
  "SoakDurations":{"M":{"nightly->edge":{"S":"00:01:00"},"edge->beta":{"S":"00:01:00"},"beta->stable":{"S":"00:01:00"},"stable->lts":{"S":"00:01:00"}}},
  "IsPromotionPaused":{"BOOL":false}}'

CLI="dotnet run --project src/ReleaseRingService/ReleaseRingService.Cli --"   # ReleaseRing__Region=us-west-2
$CLI register-candidate <ami-id>
$CLI show-state
$CLI rollback-ring beta <ami-id> "<reason>"
$CLI resume-promotions

aws autoscaling set-desired-capacity --auto-scaling-group-name <asg> --desired-capacity 1
terraform destroy
```

## Notes

- solution is too verbose for the simplicity of the idea
- core concept is basically three variables/tracks; e.g. stable, edge, other channel
- current amount of structure around this is too laborious
- implementation complexity is disproportionate to the model
- likely needs a smaller-scale rearchitecture
- goal; express channel/track behaviour with much less machinery

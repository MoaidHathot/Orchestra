import json
import sys

pr_url = 'https://dev.azure.com/msazure/One/_git/ZTS/pullrequest/15337578'
pr_title = 'RP Orchestration refactoring'
pr_author = 'Moaid Hathot'
pr_repo = 'ZTS'
pr_source = 'feature/moaid/small-refactoring'
pr_target = 'main'

# Short file names for display
def short_file(path):
    return path.split('/')[-1]

# File diffs (abbreviated for code context blocks)
file_diffs = {
    'CreateOrUpdateOrchestrator.cs': 'src/ResourceProvider/ResourceProvider.Service/Orchestration/CreateOrUpdateOrchestrationV1/CreateOrUpdateOrchestrator.cs',
    'DeleteOrchestrator.cs': 'src/ResourceProvider/ResourceProvider.Service/Orchestration/DeleteOrchestrationV1/DeleteOrchestrator.cs',
    'SetWorkloadSegmentsOrchestrator.cs': 'src/ResourceProvider/ResourceProvider.Service/Orchestration/SetWorkloadSegmentsV1/SetWorkloadSegmentsOrchestrator.cs',
    'SetWorkloadSegmentsDataProcessActivity.cs': 'src/ResourceProvider/ResourceProvider.Service/Orchestration/SetWorkloadSegmentsV1/SetWorkloadSegmentsDataProcessActivity.cs',
}

responses = [
    {
        'thread_id': 238143140,
        'draft_id': '9c65feac-24d9-4a8b-b9dd-439ac905685f',
        'file': file_diffs['CreateOrUpdateOrchestrator.cs'],
        'commenter': 'David Fineboym',
        'category': 'ConfigureAwait(true)',
        'comment': 'ConfigureAwait(true) ?',
        'reply': "Good catches, David.\n\n1. **ConfigureAwait(true)** \u2014 You're right, I missed adding `.ConfigureAwait(true)` on the `StopUponIdentityRemovalAsync` await. Will add it for consistency with the rest of the orchestrator calls.\n\n2. **StopUponIdentityRemovalAsync method** \u2014 Fair point. The method was extracted to encapsulate the \"check identity type \u2192 stop if None\" logic as a named concept, but I agree it introduces unnecessary boolean branching and indirection. I'll inline it back to the original `if (resource.Identity?.Type == ManagedIdentityType.None)` pattern with the stop logic directly in the orchestrator body \u2014 that's cleaner and more readable in the Durable Task context.",
        'action': 'Agreed \u2014 will add ConfigureAwait(true)',
        'diff_hunk': "@@ -26,7 +27,7 @@ public partial class CreateOrUpdateOrchestrator\n-            var orchestrationOptions = await GetOrchestrationOptionsAsync(context);\n+            var orchestrationOptions = await GetOrchestrationOptionsAsync(context).ConfigureAwait(true);"
    },
    {
        'thread_id': 238143187,
        'draft_id': 'ad2481a2-fcc3-4589-a47f-4c89db33dc5b',
        'file': file_diffs['CreateOrUpdateOrchestrator.cs'],
        'commenter': 'David Fineboym',
        'category': 'Redundant parameters',
        'comment': 'No need to pass these 2 as they are included in the `resource` parameter.\nSame concept applies for `StopSegmentationManagerAsync` method.',
        'reply': "Agreed \u2014 `resourceId` and `internalMetadata` are readily derivable from `resource`, so passing them separately is redundant. I'll remove these extra parameters from both `StopUponIdentityRemovalAsync` and `StopSegmentationManagerAsync`, and derive them internally within each method.",
        'action': 'Agreed \u2014 will remove redundant params',
        'diff_hunk': "@@ CreateOrUpdateOrchestrator.cs - method signatures\n  private static async Task StopUponIdentityRemovalAsync(\n      ...\n      SegmentationManagerResource resource,\n-     ResourceIdentifier resourceId,\n-     InternalMetadata internalMetadata,\n      ...)"
    },
    {
        'thread_id': 238146329,
        'draft_id': '88af9662-6898-4724-9b8f-718194162b42',
        'file': file_diffs['CreateOrUpdateOrchestrator.cs'],
        'commenter': 'David Fineboym',
        'category': 'Redundant parameters',
        'comment': 'No need to pass `resourceId` when `resource` is passed.',
        'reply': "Agreed. `resourceId` can be derived from `resource.Id` inside the method \u2014 will remove the redundant parameter from `ValidateConfigurationAsync`.",
        'action': 'Agreed \u2014 will remove redundant param',
        'diff_hunk': "@@ CreateOrUpdateOrchestrator.cs - ValidateConfigurationAsync\n  private static async Task ValidateConfigurationAsync(\n      ...\n      SegmentationManagerResource resource,\n-     ResourceIdentifier resourceId,\n      ...)"
    },
    {
        'thread_id': 238146478,
        'draft_id': 'dba5f1e9-3b95-4977-a43b-1544f5ceb6ac',
        'file': file_diffs['CreateOrUpdateOrchestrator.cs'],
        'commenter': 'David Fineboym',
        'category': 'Redundant parameters',
        'comment': 'No need to pass `resourceId` and `internalMetadata` when `resource` is passed.\nBoth for `SetupMrgAsync` and `HandleLogAnalyticsAsync`',
        'reply': "Agreed. Both `SetupMrgAsync` and `HandleLogAnalyticsAsync` receive `resource` already, so `resourceId` and `internalMetadata` can be derived internally. Will clean up both method signatures accordingly.",
        'action': 'Agreed \u2014 will remove redundant params',
        'diff_hunk': "@@ CreateOrUpdateOrchestrator.cs - SetupMrgAsync / HandleLogAnalyticsAsync\n  private static async Task SetupMrgAsync(\n      ...\n      SegmentationManagerResource resource,\n-     ResourceIdentifier resourceId,\n-     InternalMetadata internalMetadata,\n      ...)"
    },
    {
        'thread_id': 238146676,
        'draft_id': '8528df1d-5c21-47a6-873d-bba232074602',
        'file': file_diffs['CreateOrUpdateOrchestrator.cs'],
        'commenter': 'David Fineboym',
        'category': 'Correctness bug (RunVersion order)',
        'comment': "This assignment should happen after `await UpsertDataProcessAsync`, as was before, otherwise on failure from backend, we will increase the run version not sequentially on retry. Because in finally block we patch SM resource including its metadata.\nFor example, backend has data-plane validation, so on bad input it will fail the request, customer can fix it, and re-PATCH the SM resource. Run Version is a customer visible column in target LAW table.",
        'reply': "You're absolutely right \u2014 this is a correctness issue I introduced during the refactoring. The `internalMetadata.RunVersion = runVersion` assignment must happen **after** `await UpsertDataProcessAsync` succeeds, not before.\n\nAs you explained, the RunVersion gets persisted in the `finally` block when we patch the SM resource. If `UpsertDataProcessAsync` fails (e.g., backend data-plane validation), the incremented RunVersion would already be saved, and on retry with a scope change it would increment again \u2014 making RunVersion non-sequential for the customer-visible LAW table column.\n\nI'll move the assignment back to after `UpsertDataProcessAsync` as it was originally. Thanks for catching this \u2014 it's an important correctness guard.",
        'action': 'Acknowledged regression \u2014 will fix ordering',
        'diff_hunk': "@@ CreateOrUpdateOrchestrator.cs - RunVersion assignment\n+            internalMetadata.RunVersion = runVersion;  // BUG: moved before UpsertDataProcessAsync\n+\n+            await UpsertDataProcessAsync(logger, context, ...)\n             // Should be: UpsertDataProcess first, THEN assign RunVersion"
    },
    {
        'thread_id': 238150072,
        'draft_id': '308d5b3b-d4b4-4089-846a-be50da655df1',
        'file': file_diffs['CreateOrUpdateOrchestrator.cs'],
        'commenter': 'David Fineboym',
        'category': 'Method unification',
        'comment': 'We better unify `UpdateOperationStatusToProvisioningAsync` and `UpdateOperationStatusAsync` into a single method and unify their logs.\nSame is true for `DeleteOrchestrator` that has `UpdateOperationStatusAsync` and `UpdateOperationStatusToDeletingAsync`.',
        'reply': "Good suggestion. `UpdateOperationStatusToProvisioningAsync` and `UpdateOperationStatusAsync` are structurally identical \u2014 they both construct an `AsyncOperationActivityInput` and call the same activity. The only difference is the `OperationStatusCode` value (explicit `Provisioning` vs. inferred from `Error`).\n\nI'll unify them into a single method that accepts an optional `OperationStatusCode` parameter. Same treatment for `DeleteOrchestrator`'s `UpdateOperationStatusToDeletingAsync` / `UpdateOperationStatusAsync` pair. Logging will also be consolidated.",
        'action': 'Agreed \u2014 will consolidate methods',
        'diff_hunk': "@@ CreateOrUpdateOrchestrator.cs - duplicate methods\n+    private static async Task UpdateOperationStatusToProvisioningAsync(...)\n+    {\n+        // Nearly identical to UpdateOperationStatusAsync below\n+    }\n+\n+    private static async Task UpdateOperationStatusAsync(...)\n+    {\n+        // Same structure, different OperationStatusCode\n+    }"
    },
    {
        'thread_id': 238151753,
        'draft_id': '499187ba-693f-48f7-8c32-2513a566023e',
        'file': file_diffs['CreateOrUpdateOrchestrator.cs'],
        'commenter': 'David Fineboym',
        'category': 'Error-forwarding design',
        'comment': "The method accepts customerFacingError only to pass it through unchanged on the success path and replace it on the failure path. This makes the method responsible for error-forwarding logic that isn't its concern.\nThe more conventional approach is to have the method return only its own result and let the caller handle the merging.\n```\nfinally\n{\n    var (patchError, innerExceptionToRethrow) = await TryPatch...;\n    customerFacingError = patchError ?? customerFacingError;\n    ...\n}\n```",
        'reply': "You're right \u2014 the current signature makes `TryPatchSegmentationManagerAsync` responsible for error-forwarding logic that isn't really its concern. Accepting `customerFacingError` just to pass it through unchanged on success is unnecessary coupling.\n\nI'll refactor to your suggested pattern:\n\n```csharp\nfinally\n{\n    var (patchError, innerExceptionToRethrow) = await TryPatchSegmentationManagerAsync(...);\n    customerFacingError = patchError ?? customerFacingError;\n    // ...\n}\n```\n\nThis way the method only returns its own result (patch error or null), and the caller handles the merging. Cleaner separation of concerns.",
        'action': 'Agreed \u2014 will adopt caller-merge pattern',
        'diff_hunk': "@@ CreateOrUpdateOrchestrator.cs - TryPatchSegmentationManagerAsync\n  private static async Task<(ErrorDefinition?, Exception?)>\n      TryPatchSegmentationManagerAsync(\n          ...\n-         ErrorDefinition? customerFacingError,  // pass-through coupling\n          ...)"
    },
    {
        'thread_id': 238151803,
        'draft_id': '8e5a104a-3339-488c-9be6-37a7a912bd24',
        'file': file_diffs['CreateOrUpdateOrchestrator.cs'],
        'commenter': 'David Fineboym',
        'category': 'Redundant parameters',
        'comment': 'No need to pass `resourceId` when we already pass `resource` param.',
        'reply': "Agreed. `PatchSegmentationManagerAsync` already receives `resource`, so `resourceId` is redundant \u2014 will derive it inside the method and remove the parameter.",
        'action': 'Agreed \u2014 will remove redundant param',
        'diff_hunk': "@@ CreateOrUpdateOrchestrator.cs - PatchSegmentationManagerAsync\n  private static async Task PatchSegmentationManagerAsync(\n      ...\n      SegmentationManagerResource resource,\n-     ResourceIdentifier resourceId,\n      ...)"
    },
    {
        'thread_id': 238152203,
        'draft_id': '3443b777-53a0-44f7-9987-8632a09f9bed',
        'file': file_diffs['CreateOrUpdateOrchestrator.cs'],
        'commenter': 'David Fineboym',
        'category': 'Redundant parameters',
        'comment': 'No need to pass `resourceId` when passing `resource` param',
        'reply': "Agreed \u2014 same pattern as the other methods. Will remove the redundant `resourceId` parameter and derive it from `resource` internally.",
        'action': 'Agreed \u2014 will remove redundant param',
        'diff_hunk': "@@ CreateOrUpdateOrchestrator.cs - another method\n  private static async Task SomeMethodAsync(\n      ...\n      SegmentationManagerResource resource,\n-     ResourceIdentifier resourceId,\n      ...)"
    },
    {
        'thread_id': 238152752,
        'draft_id': '19b824c5-aadc-4def-83ca-b7dbf9ee725c',
        'file': file_diffs['CreateOrUpdateOrchestrator.cs'],
        'commenter': 'David Fineboym',
        'category': 'ConfigureAwait(true)',
        'comment': 'ConfigureAwait(true)?\nand below?',
        'reply': "Yes \u2014 I'll add `.ConfigureAwait(true)` consistently to all `await` calls in the orchestrator methods. Given Durable Task replay semantics, being explicit about continuation context is the right approach. Will fix this call and the ones below.",
        'action': 'Agreed \u2014 will add ConfigureAwait(true)',
        'diff_hunk': "@@ CreateOrUpdateOrchestrator.cs - await calls\n-            await ValidateConfigurationAsync(...)\n+            await ValidateConfigurationAsync(...).ConfigureAwait(true)"
    },
    {
        'thread_id': 238153707,
        'draft_id': '406ddb3d-30c5-455c-b0d7-0e631f3c4dc6',
        'file': file_diffs['DeleteOrchestrator.cs'],
        'commenter': 'David Fineboym',
        'category': 'ConfigureAwait(true)',
        'comment': 'ConfigureAwait(true)?',
        'reply': "Good catch \u2014 the `GetOrchestrationOptionsAsync` helper is missing `.ConfigureAwait(true)` on the inner await. Will add it for consistency with the rest of the orchestrator.",
        'action': 'Agreed \u2014 will add ConfigureAwait(true)',
        'diff_hunk': "@@ DeleteOrchestrator.cs - GetOrchestrationOptionsAsync\n     private async Task<GetOrchestrationOptionsActivityResult>\n         GetOrchestrationOptionsAsync(TaskOrchestrationContext context)\n     {\n-        return await context.CallActivityAsync<...>(options: taskOptions);\n+        return await context.CallActivityAsync<...>(options: taskOptions).ConfigureAwait(true);\n     }"
    },
    {
        'thread_id': 238153862,
        'draft_id': 'f40dc291-959d-4523-9141-e73404a8dfef',
        'file': file_diffs['SetWorkloadSegmentsDataProcessActivity.cs'],
        'commenter': 'David Fineboym',
        'category': 'Expression-bodied \u2192 statement blocks',
        'comment': 'Can you please use statement blocks here as well (instead of expression bodied methods)? Here and below in `ToWorkloadSegmentsV1`',
        'reply': "Sure \u2014 I'll convert both `CreateUpsertRequest` and `ToWorkloadSegmentsV1` from expression-bodied form to statement blocks with explicit `return` statements for consistency with the rest of the codebase.",
        'action': 'Agreed \u2014 will convert to statement blocks',
        'diff_hunk': "@@ SetWorkloadSegmentsDataProcessActivity.cs\n+    private static WorkloadSegmentsUpsertRequestV1 CreateUpsertRequest(...)\n+        => new()  // expression-bodied\n+    {\n+        ...\n+    };\n+\n+    private static WorkloadSegmentsV1 ToWorkloadSegmentsV1(...) => new()  // expression-bodied\n+    {\n+        ...\n+    };"
    },
    {
        'thread_id': 238154096,
        'draft_id': 'e3bacfdb-b242-4c00-926f-9ee435b6a198',
        'file': file_diffs['SetWorkloadSegmentsOrchestrator.cs'],
        'commenter': 'David Fineboym',
        'category': 'Redundant parameters',
        'comment': "No need to pass `segmentationManagerResourceId` when it's included in passed `parameters`.\nSame for `SetWorkloadSegmentsDataProcessAsync` below.",
        'reply': "Agreed. `segmentationManagerResourceId` is just `parameters.SegmentationManagerResourceId` \u2014 I'll remove it as a separate parameter from both `ValidatePayloadAsync` and `SetWorkloadSegmentsDataProcessAsync`, and derive it inside each method.",
        'action': 'Agreed \u2014 will remove redundant param',
        'diff_hunk': "@@ SetWorkloadSegmentsOrchestrator.cs\n+    private static async Task<...> ValidatePayloadAsync(\n         ILogger logger,\n         TaskOrchestrationContext context,\n         SetWorkloadSegmentsOrchestrationParameters parameters,\n-        ResourceIdentifier segmentationManagerResourceId,\n         TaskOptions activityTaskOptions)"
    },
    {
        'thread_id': 238154248,
        'draft_id': '6eed1758-50ce-4021-816e-741e34179a10',
        'file': file_diffs['SetWorkloadSegmentsOrchestrator.cs'],
        'commenter': 'David Fineboym',
        'category': 'ConfigureAwait(true)',
        'comment': 'ConfigureAwait(true)?',
        'reply': "Yes \u2014 same as the other orchestrators. I'll add `.ConfigureAwait(true)` to all `await` calls in the `SetWorkloadSegmentsOrchestrator` helper methods (including `GetOrchestrationOptionsAsync`, `UpdateOperationStatusAsync`, and `SetWorkloadSegmentsDataProcessAsync`) for Durable Task replay safety.",
        'action': 'Agreed \u2014 will add ConfigureAwait(true)',
        'diff_hunk': "@@ SetWorkloadSegmentsOrchestrator.cs - GetOrchestrationOptionsAsync\n     private async Task<GetOrchestrationOptionsActivityResult>\n         GetOrchestrationOptionsAsync(TaskOrchestrationContext context)\n     {\n-        return await context.CallActivityAsync<...>(options: taskOptions);\n+        return await context.CallActivityAsync<...>(options: taskOptions).ConfigureAwait(true);\n     }"
    },
]

# Build per-response sections
response_sections = []
for i, r in enumerate(responses, 1):
    section_content = []
    
    # a) Details keyValue
    pairs = {
        'Thread ID': str(r['thread_id']),
        'Commenter': r['commenter'],
        'File': short_file(r['file']),
        'Category': r['category'],
        'Action': r['action'],
        'Agent': 'PR Author Assistant',
        'Reply Draft ID': r['draft_id'],
    }
    section_content.append({
        'type': 'keyValue',
        'label': 'Details',
        'pairs': pairs
    })
    
    # b) Original Comment
    section_content.append({
        'type': 'markdown',
        'label': 'Original Comment',
        'body': f"> {r['comment']}"
    })
    
    # c) AI Response
    section_content.append({
        'type': 'markdown',
        'label': 'AI Response',
        'body': r['reply']
    })
    
    # d) Code Context (diff hunk)
    section_content.append({
        'type': 'code',
        'label': 'Code Context',
        'language': 'diff',
        'filename': short_file(r['file']),
        'body': r['diff_hunk']
    })
    
    # f) Per-response actions
    section_actions = [
        {
            'label': 'Approve Reply',
            'style': 'success',
            'command': {
                'type': 'cli',
                'program': 'pwsh',
                'args': ['-Command', f"powerreview approve-draft --pr-url '{pr_url}' --draft-id '{r['draft_id']}'"]
            }
        },
        {
            'label': 'Delete Reply',
            'style': 'danger',
            'confirmMessage': f"Delete draft reply to thread {r['thread_id']}?",
            'command': {
                'type': 'cli',
                'program': 'pwsh',
                'args': ['-Command', f"powerreview delete-draft --pr-url '{pr_url}' --draft-id '{r['draft_id']}'"]
            }
        }
    ]
    
    response_sections.append({
        'type': 'section',
        'title': f"#{i}: {r['category']} \u2014 {short_file(r['file'])}",
        'content': section_content,
        'actions': section_actions
    })

# Build full entry
entry = {
    'schemaVersion': '1',
    'type': 'pr-comment-response',
    'source': 'pr-comment-responder',
    'title': f"Comment Responses: {pr_title}",
    'subtitle': f"14 draft replies to David Fineboym's review comments",
    'severity': 'low',
    'icon': 'message-square-reply',
    'tags': ['pr-response', 'comment-response', 'ZTS', 'orchestration-refactoring'],
    'content': [
        # Alert block (optional - include note about stale drafts)
        {
            'type': 'alert',
            'level': 'warning',
            'body': '44 stale draft replies from previous orchestration runs are present. Consider using "Delete All" to clean up before submitting new replies.'
        },
        # Pull Request keyValue (REQUIRED)
        {
            'type': 'keyValue',
            'label': 'Pull Request',
            'pairs': {
                'Title': pr_title,
                'Repository': pr_repo,
                'Branch': f"{pr_source} \u2192 {pr_target}",
                'Author': pr_author,
                'Reviewer': 'David Fineboym',
                'PR URL': pr_url
            }
        },
        # Response Summary table (REQUIRED)
        {
            'type': 'table',
            'label': 'Response Summary',
            'columns': ['Action', 'Count'],
            'rows': [
                ['Draft Replies', '14'],
                ['Code Fixes', '0'],
                ["Won't Fix", '0'],
                ['Total Responses', '14'],
                ['Stale Drafts (cleanup needed)', '44']
            ]
        },
        # Comment Responses section (REQUIRED)
        {
            'type': 'section',
            'title': 'Comment Responses',
            'content': [
                {
                    'type': 'table',
                    'label': 'Response Breakdown by Category',
                    'columns': ['Category', 'Count', 'Action'],
                    'rows': [
                        ['Redundant parameters', '6', 'Agreed \u2014 will remove and derive internally'],
                        ['ConfigureAwait(true)', '4', 'Agreed \u2014 will add for Durable Task consistency'],
                        ['Correctness bug (RunVersion order)', '1', 'Acknowledged regression \u2014 will fix ordering'],
                        ['Method unification', '1', 'Agreed \u2014 will consolidate into single method'],
                        ['Error-forwarding design', '1', 'Agreed \u2014 will adopt caller-merge pattern'],
                        ['Expression-bodied \u2192 statement blocks', '1', 'Agreed \u2014 will convert for consistency'],
                    ]
                },
                *response_sections
            ]
        },
        # Summary markdown
        {
            'type': 'markdown',
            'label': 'Summary',
            'body': "All **14 unaddressed comments** from David Fineboym have been responded to with draft replies. The comments span 4 files across 3 orchestrators (`CreateOrUpdateOrchestrator`, `DeleteOrchestrator`, `SetWorkloadSegmentsOrchestrator`) and 1 activity (`SetWorkloadSegmentsDataProcessActivity`). One comment identified a **correctness regression** where `RunVersion` assignment was moved before `UpsertDataProcessAsync`, which could cause non-sequential version numbers on retry. All replies agree with the reviewer's feedback and commit to making the suggested changes."
        },
        # View PR link (REQUIRED)
        {
            'type': 'link',
            'label': 'View PR',
            'url': pr_url,
            'body': f"{pr_title} (PR #15337578)"
        }
    ],
    'actions': [
        {
            'label': 'Open PR',
            'style': 'primary',
            'command': {
                'type': 'cli',
                'program': 'pwsh',
                'args': ['-Command', f"Start-Process '{pr_url}'"]
            }
        },
        {
            'label': 'Approve All Replies',
            'style': 'success',
            'confirmMessage': 'Approve all 14 draft replies for submission?',
            'command': {
                'type': 'cli',
                'program': 'pwsh',
                'args': ['-Command', f"powerreview approve-all-drafts --pr-url '{pr_url}'"]
            }
        },
        {
            'label': 'Submit Replies',
            'style': 'success',
            'confirmMessage': 'Submit all approved replies to the PR? This will post them as comments visible to reviewers.',
            'command': {
                'type': 'cli',
                'program': 'pwsh',
                'args': ['-Command', f"powerreview submit-drafts --pr-url '{pr_url}'"]
            },
            'onSuccess': 'archive'
        },
        {
            'label': 'Approve All Proposals',
            'style': 'success',
            'command': {
                'type': 'cli',
                'program': 'pwsh',
                'args': ['-Command', f"powerreview approve-all-proposals --pr-url '{pr_url}'"]
            }
        },
        {
            'label': 'Apply All Proposals',
            'style': 'success',
            'command': {
                'type': 'cli',
                'program': 'pwsh',
                'args': ['-Command', f"powerreview apply-all-proposals --pr-url '{pr_url}'"]
            }
        },
        {
            'label': 'Delete All',
            'style': 'danger',
            'confirmMessage': 'Delete ALL 58 draft replies (14 new + 44 stale)? This cannot be undone.',
            'command': {
                'type': 'cli',
                'program': 'pwsh',
                'args': ['-Command', f"powerreview delete-all-drafts --pr-url '{pr_url}'"]
            },
            'onSuccess': 'archive'
        }
    ]
}

print(json.dumps(entry, indent=2))

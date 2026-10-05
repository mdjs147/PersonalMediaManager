#!/usr/bin/env python3
"""Published-plan successor controls. Declarations are not authentication."""
from __future__ import annotations
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import unittest
import pmm_governance as g
v=g.v
need=g.need
ROOT=Path(__file__).resolve().parents[1]
REPOSITORY=g.REPOSITORY
STAGE=g.STAGE
ANCHOR='a59311ae16b08d0268babc933abf7403a14a315f'
STATE='docs/agents/workflow-state.json'
MANIFEST=g.MANIFEST
PHASES={'governance-maintenance':'governance','development-foundation':'foundation','requirements-design':'requirements','development-offline-domain':'offline-domain'}
DOTNET_PHASES={'development-foundation','development-offline-domain'}
CORE=('scripts/pmm_workflow.py','scripts/pmm_governance.py','docs/agents/readiness/validate_readiness.py','docs/agents/readiness/readiness.schema.json')

def blob_bytes(data):return hashlib.sha1(b'blob '+str(len(data)).encode()+b'\0'+data).hexdigest()
def unique_object(pairs):
    result={}
    for key,value in pairs:
        need(key not in result,'Duplicate JSON key: '+key);result[key]=value
    return result

def parse_json(text):return json.loads(text,object_pairs_hook=unique_object)
def read_json(path):return parse_json(path.read_text(encoding='utf-8'))
def object_json(root,commit,path):
    v.oid(commit);v.relative_path(path)
    return parse_json(git(root,'show',commit+':'+path))
def ancestor(root,a,b):return git(root,'merge-base','--is-ancestor',a,b,optional=True) is not None
# Keep optional support explicit; validation errors must not be swallowed.
def git(root,*args,optional=False):return v.git(root,*args,optional=optional)
def clean_identity(value):
    need(isinstance(value,dict) and set(value)=={'actor_id','execution_id','model'},'Invalid actor declaration')
    for key in ('actor_id','execution_id'):
        need(isinstance(value[key],str) and value[key].strip() and value[key]!='unassigned','Actor/execution is unassigned')
    need(value['model'] is None or isinstance(value['model'],str),'Invalid actual model')
    return value

def independent(report,authors):
    need(report.get('result')=='approved' and report.get('blocking_findings') in (0,[]),'Independent review missing/blocked')
    reviewer=clean_identity(report.get('reviewer'))
    for author in authors:
        for key in ('actor_id','execution_id'):
            need(reviewer[key].strip().casefold()!=author[key].strip().casefold(),'Review not independent: '+key)
    v.timestamp(report.get('observed_at',report.get('reviewed_at')))

def remote(root,live=True,expected_old=None):
    need(git(root,'remote').splitlines()==['origin'],'Unexpected remote')
    for args in [('remote','get-url','--all','origin'),('remote','get-url','--push','--all','origin')]:
        need(git(root,*args).splitlines()==[REPOSITORY],'Wrong actual remote')
    need(git(root,'symbolic-ref','refs/remotes/origin/HEAD')=='refs/remotes/origin/main','Wrong default branch')
    need(git(root,'rev-parse','--is-shallow-repository')=='false','Shallow history')
    need(not git(root,'for-each-ref','--format=%(refname)','refs/replace'),'Replacement refs are not trusted history')
    graft=Path(git(root,'rev-parse','--git-path','info/grafts'));graft=graft if graft.is_absolute() else root/graft
    need(not graft.exists(),'Grafted history is not trusted')
    cached=git(root,'rev-parse','refs/remotes/origin/'+STAGE.removeprefix('refs/heads/'))
    if live:
        result=g.run(['git','ls-remote','--symref','origin','HEAD','refs/heads/main',STAGE],root)
        need(result.returncode==0,'Live remote unavailable')
        lines=result.stdout.splitlines();need('ref: refs/heads/main\tHEAD' in lines,'Live default changed')
        refs={line.split('\t')[1]:line.split('\t')[0] for line in lines if '\t' in line and not line.startswith('ref: ')}
        need(refs.get(STAGE)==cached,'Live/cached stage drift; fetch and review')
        need(refs.get('refs/heads/main')==git(root,'rev-parse','refs/remotes/origin/main'),'Live/cached main drift')
        print('LIVE_REMOTE_OBSERVED '+json.dumps(refs,sort_keys=True))
    if expected_old is not None:need(cached==expected_old,'Remote target changed since observation')
    return cached

def descriptor(plan,task_id):
    need(plan.get('schema_version')==1 and plan.get('kind')=='successor-plan-publication' and plan.get('status')=='planned','Invalid plan publication')
    need(plan.get('repository')==REPOSITORY and plan.get('target_ref')==STAGE and plan.get('default_ref')=='refs/heads/main','Plan target changed')
    need(task_id in plan.get('tasks',{}),'Unknown task')
    task=plan['tasks'][task_id]
    need(task.get('status')=='planned' and task.get('business_allowed') is False,'Plan grants premature business/activation')
    need(task.get('phase') in PHASES and isinstance(task.get('purpose'),str) and PHASES[task['phase']]==task['purpose'],'Unknown phase/purpose')
    need(isinstance(task.get('applicability'),str) and task['applicability'].strip(),'Missing applicability')
    need(task['task_branch'].startswith('refs/heads/dot/task/') and task['task_branch']!=STAGE,'Invalid task branch')
    paths=task['allowed_exact']+task['allowed_prefixes'];need(paths,'Empty task scope')
    for path in paths:
        v.relative_path(path.rstrip('/'))
        need(path not in ('','.', 'docs/','scripts/','src/','tests/'),'Unbounded scope')
        need(not path.startswith(('.git/','.github/workflows/')) or path in task['allowed_exact'],'Unbounded sensitive scope')
    if task['purpose']=='offline-domain':
        need(not any('src/'.startswith(x) for x in task['allowed_prefixes']),'Offline-domain prefix overlaps product source root')
        need(all(not x.startswith('src/') or x.startswith('src/PersonalMediaManager.Catalog/') for x in paths),'Offline-domain source must stay inside Catalog/')
    elif task['purpose']!='foundation':need(not any(x.startswith('src/') for x in paths),'Non-foundation task grants product source')
    return task

def load_task(root,live=False,allow_unpublished_activation=False):
    state=read_json(v.confined(root,STATE));need(state.get('schema_version')==1 and state.get('anchor')==ANCHOR,'Wrong workflow anchor')
    stage=remote(root,live);need(ancestor(root,ANCHOR,stage),'Stage outside accepted history')
    plan_commit=state['plan_commit'];need(ancestor(root,plan_commit,stage),'Unpublished plan')
    need(ancestor(root,plan_commit,'HEAD'),'Worktree does not descend from plan')
    plan=object_json(root,plan_commit,state['plan_path']);task=descriptor(plan,state['active_task'])
    stage_state_raw=git(root,'show',stage+':'+STATE,optional=True)
    if not allow_unpublished_activation:
        if stage_state_raw is None:
            need(plan.get('bootstrap',{}).get('fixed_base')==ANCHOR and plan['expected_old']==ANCHOR and task['dependencies']==['BA-001'] and task['purpose']=='governance','Activation has not been published')
        else:
            stage_state=parse_json(stage_state_raw)
            need(stage_state==state,'Activation has not been published unchanged')
    need(task['card'] in plan['publication_paths'],'Task card absent from publication')
    if task['applicability'].startswith('docs/'):
        need(task['applicability'] in plan['publication_paths'],'Applicability not frozen in publication')
    for path in plan['publication_paths']:
        expected=git(root,'rev-parse',plan_commit+':'+path)
        need(v.blob(root,path)==expected and git(root,'rev-parse',stage+':'+path)==expected,'Frozen published plan changed: '+path)
    for path,expected in g.FROZEN.items():need(v.blob(root,path)==expected,'Historical BA001 card changed')
    activation=state['activation'];need(ancestor(root,plan_commit,activation['base_commit']) and ancestor(root,activation['base_commit'],stage),'Task activation base is not stage-derived')
    if stage_state_raw is None:need(activation['base_commit']==plan_commit,'Bootstrap base changed')
    need(activation['task_id']==state['active_task'] and activation['plan_commit']==plan_commit,'Activation task mismatch')
    need(activation['phase']==task['phase'] and activation['purpose']==task['purpose'],'Activation phase/purpose self-grant')
    author=clean_identity(activation['author']);actors=activation['authorized_executions'];need(author in actors and actors,'Missing actual assigned author')
    for actor in actors:clean_identity(actor)
    if task['author']['execution_id']!='unassigned':need(author==task['author'],'Published author changed')
    report=read_json(v.confined(root,state['activation_review']));independent(report,actors)
    need(report['state_blob']==v.blob(root,STATE) and report['task_id']==state['active_task'] and report['plan_commit']==plan_commit,'Stale activation review')
    for dependency in task['dependencies']:
        receipt=activation['predecessors'].get(dependency);need(receipt is not None,'Missing predecessor acceptance')
        commit=receipt['commit'];need(ancestor(root,commit,stage),'Predecessor not published')
        if dependency=='BA-001':need(commit==ANCHOR,'Historical acceptance changed')
        else:
            previous=object_json(root,commit,STATE);need(previous['active_task']==dependency,'Wrong predecessor task')
            previous_plan=object_json(root,previous['plan_commit'],previous['plan_path'])
            previous_task=descriptor(previous_plan,dependency)
            expected=binding(root,previous['activation']['base_commit'],commit)
            expected['task_path']=previous_task['card']
            approval=read_json(v.confined(root,receipt['report']))
            verification=read_json(v.confined(root,receipt['verification_report']))
            v.validate_document(approval,'review_report');v.validate_document(verification,'verification_report')
            for evidence in (approval,verification):
                for key,value in expected.items():need(evidence.get(key)==value,'Stale predecessor full binding: '+key)
            independent(approval,previous['activation']['authorized_executions'])
            need(verification['result']=='passed' and verification['checks'],'Missing predecessor test report')
            outputs={receipt['report']:v.blob(root,receipt['report']),receipt['verification_report']:v.blob(root,receipt['verification_report'])}
            for check in verification['checks']:
                need(check['result']=='passed' and check['exit_code']==0,'Failed predecessor test evidence')
                output=check['output'];need(v.confined(root,output).read_text().strip(),'Empty predecessor output');outputs[output]=v.blob(root,output)
            receipt_review=read_json(v.confined(root,receipt['acceptance_review']))
            independent(receipt_review,previous['activation']['authorized_executions'])
            for key,value in expected.items():need(receipt_review.get(key)==value,'Stale predecessor receipt binding: '+key)
            need(receipt_review['evidence_blobs']==outputs,'Predecessor evidence changed after independent acceptance')

    return state,task

def scope(root,state,task,extra_untracked=(),published_additions=()):
    tracked=set()
    for commit in git(root,'rev-list',state['activation']['base_commit']+'..HEAD').splitlines():
        need(len(git(root,'rev-list','--parents','-n','1',commit).split())==2,'Unapproved merge/root in task history')
        tracked.update(git(root,'diff-tree','--no-commit-id','--name-only','-r','--no-renames',commit).splitlines())
    for args in [('diff','--name-only','--no-renames',state['activation']['base_commit'],'HEAD'),('diff','--name-only','--no-renames'),('diff','--cached','--name-only','--no-renames')]:tracked.update(git(root,*args).splitlines())
    untracked=set(git(root,'ls-files','--others','--exclude-standard').splitlines())
    # Final sidecars cannot hide committed or staged changes.
    paths=(tracked-set(published_additions)) | (untracked-set(extra_untracked))
    for path in paths:
        v.relative_path(path)
        need(path in task['allowed_exact'] or any(path.startswith(x) for x in task['allowed_prefixes']),'Path exceeds published task scope: '+path)
    print('SCOPE_VERIFIED '+str(len(paths))+' '+state['active_task']);return paths

def owner(root,state,task,actor,execution):
    need(any(x['actor_id']==actor and x['execution_id']==execution for x in state['activation']['authorized_executions']),'Unauthorized actor/execution')
    need(git(root,'config','--get','pmm.actorId',optional=True)==actor and git(root,'config','--get','pmm.executionId',optional=True)==execution,'Worktree ownership not established')
    branch=git(root,'symbolic-ref','--quiet','HEAD',optional=True);need(branch==task['task_branch'],'Wrong task branch/detached worktree')
    entries=git(root,'worktree','list','--porcelain').split('\n\n')
    owned=[x for x in entries if 'worktree '+str(root) in x.splitlines()]
    need(len(owned)==1 and 'branch '+branch in owned[0].splitlines() and not any(x.startswith(('locked','prunable')) for x in owned[0].splitlines()),'Unsafe/unowned worktree')
    print('ACTOR_WORKTREE_VERIFIED '+json.dumps({'actor_id':actor,'execution_id':execution,'worktree':str(root),'branch':branch,'task':state['active_task']},sort_keys=True))

def context(root,purpose,allow_pending=False,allow_unpublished_activation=False):
    state,task=load_task(root,allow_unpublished_activation=allow_unpublished_activation)
    need(purpose!='business' and purpose==task['purpose'],'Business or wrong task purpose rejected')
    manifest=read_json(v.confined(root,MANIFEST))
    if task['phase'] in DOTNET_PHASES:
        sdk=parse_json(v.confined(root,'global.json').read_text()).get('sdk',{})
        need(re.fullmatch(r'10\.\d+\.\d+',sdk.get('version','')) is not None and sdk.get('allowPrerelease') is False,'Foundation needs a reviewed stable SDK10 global.json pin')
        result=g.run(['dotnet','--version'],root);need(result.returncode==0 and re.fullmatch(r'10\.\d+\.\d+[^\s]*\s*',result.stdout),'Unreviewed .NET SDK; foundation blocked')
        print('FOUNDATION_SDK_OBSERVED '+result.stdout.strip())
    if allow_pending and manifest['status']=='pending':
        need(task['purpose']=='governance','Only published governance repair may use pending context')
        v.validate_document(manifest,'manifest');v.observed_context(root,manifest)
        print('GOVERNANCE_REPAIR_ONLY; no ready or product claim')
    else:
        v.check_context(root,manifest)
        report=read_json(v.confined(root,state['activation_review']))
        need(report['context']==v.observed_context(root,manifest),'Activation context is stale')
        print('SUCCESSOR_CONTEXT_VERIFIED '+state['active_task'])

def public_tree(root,extra_untracked=()):
    names=set(git(root,'ls-files','--cached','--others','--exclude-standard').splitlines())
    for name in names:
        path=v.confined(root,name,nonempty=False)
        need(path.suffix.lower() not in {'.dll','.exe','.sqlite','.db','.zip','.safetensors','.pyc'},'Binary/runtime data in public tree')
        text=path.read_text(encoding='utf-8')
        need('\x00' not in text,'Binary NUL content: '+name)
        need(not re.search(r'gh[pousr]_[A-Za-z0-9]{30,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|libfile_[a-z0-9]{20,}',text),'Private/credential-like content: '+name)
    print('PUBLIC_TREE_SCREENED '+str(len(names))+'; independent semantic review still required')

def audit_candidate(root,candidate):
    data=v.git(root,'ls-tree','-rz',candidate,binary=True)
    for row in data.split(b'\0'):
        if not row:continue
        header,path_bytes=row.split(b'\t',1);mode,kind,oid=header.decode().split();path=path_bytes.decode('utf-8');v.relative_path(path)
        need(mode in {'100644','100755'} and kind=='blob','Nonregular candidate path: '+path)
        need(Path(path).suffix.lower() not in {'.dll','.exe','.sqlite','.db','.zip','.safetensors','.pyc'},'Binary/runtime candidate input')
        text=v.git(root,'cat-file','blob',oid,binary=True).decode('utf-8')
        need('\x00' not in text,'Binary NUL candidate content: '+path)
        need(not re.search(r'gh[pousr]_[A-Za-z0-9]{30,}|-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----|libfile_[a-z0-9]{20,}',text),'Secret/private candidate input: '+path)
    print('CANDIDATE_TREE_SCREENED '+candidate)

def preflight(root,phase,actor,execution,purpose,live=True):
    need(phase in {'first-write','recover','commit'},'Unknown lifecycle phase')
    state,task=load_task(root,live);owner(root,state,task,actor,execution);scope(root,state,task)
    need(ancestor(root,remote(root,False),'HEAD'),'Worktree is behind/diverged from published stage')
    g.runtime(root);g.baseline(root,online=live);g.documentation(root);public_tree(root)
    if phase in {'first-write','recover'}:need(git(root,'status','--porcelain')=='','First-write/recovery requires clean owned worktree')
    if phase=='commit':need(git(root,'diff','--name-only')=='','Unstaged changes before commit')
    context(root,purpose,allow_pending=True)
    print('PMM_SUCCESSOR_GATE_VERIFIED '+phase+' '+state['active_task'])

def binding(root,old,candidate):
    v.oid(old);v.oid(candidate);need(ancestor(root,old,candidate) and old!=candidate,'Not a nonempty fast-forward candidate')
    return {'base_commit':old,'candidate_commit':candidate,'candidate_tree':git(root,'rev-parse',candidate+'^{tree}'),'diff_paths':sorted(git(root,'diff','--name-only','--no-renames',old,candidate).splitlines())}

def checked_review(root,path,expected,authors):
    review=read_json(v.confined(root,path));independent(review,authors)
    for key,value in expected.items():need(review.get(key)==value,'Stale review '+key)
    return review

def trusted_controller(root,old):
    for path in CORE:
        source=ROOT/path
        need(blob_bytes(source.read_bytes())==git(root,'rev-parse',old+':'+path),'Run controller exported from the trusted old tree, not candidate code')

def publication_owner(root,actor,execution,branch):
    need(branch.startswith('refs/heads/dot/task/') and branch!=STAGE,'Forbidden publication branch')
    need(git(root,'symbolic-ref','--quiet','HEAD',optional=True)==branch,'Wrong publication branch')
    need(git(root,'config','--get','pmm.actorId',optional=True)==actor and git(root,'config','--get','pmm.executionId',optional=True)==execution,'Publication worktree ownership not established')
    entries=git(root,'worktree','list','--porcelain').split('\n\n')
    owned=[e for e in entries if 'worktree '+str(root) in e.splitlines()]
    need(len(owned)==1 and 'branch '+branch in owned[0].splitlines() and not any(line.startswith(('locked','prunable')) for line in owned[0].splitlines()),'Unsafe publication worktree')

def activation_base(root,state,prior,old):
    if state['active_task']==prior['active_task']:
        need(state['plan_commit']==prior['plan_commit'] and state['plan_path']==prior['plan_path'],'Handoff cannot change task plan')
        old_activation=prior['activation'];new_activation=state['activation']
        for key in ('base_commit','task_id','plan_commit','phase','purpose','author','predecessors'):
            need(new_activation[key]==old_activation[key],'Handoff cannot change scope/base: '+key)
        need(new_activation['authorized_executions']!=old_activation['authorized_executions'],'Handoff must change assigned executions')
        need(all(x in new_activation['authorized_executions'] for x in old_activation['authorized_executions']),'Handoff cannot erase historical participants')
    else:
        need(state['activation']['base_commit']==old,'Activation construction base must be exact previous stage')

def prepare(root,operation,old,actor,execution,plan_path=None,live=True):
    """Registered pre-commit path for plan/activation candidates, before final review."""
    need(operation in {'plan','activate'},'Unknown preparation operation')
    trusted_controller(root,old);remote(root,live,old)
    need(git(root,'rev-parse','HEAD')==old,'Preparation must branch from exact old stage')
    need(git(root,'diff','--name-only')=='','Preparation has unstaged changes')
    need(not git(root,'ls-files','--others','--exclude-standard'),'Preparation has undeclared untracked inputs')
    tree=git(root,'write-tree');paths=sorted(git(root,'diff','--cached','--name-only','--no-renames',old).splitlines());need(paths,'Empty preparation')
    prior=object_json(root,old,STATE)
    if operation=='plan':need(any(x['actor_id']==actor and x['execution_id']==execution for x in prior['activation']['authorized_executions']),'Unauthorized preparation initiator')
    if operation=='plan':
        need(plan_path.startswith('docs/workflow/') and plan_path.endswith('-plan-publication.json'),'Invalid plan publication filename')
        plan=object_json(root,tree,plan_path);need(not plan.get('bootstrap') and plan['expected_old']==old,'Invalid ordinary plan base/bootstrap')
        publication_owner(root,actor,execution,plan['publication_branch'])
        need(plan['author']['actor_id']==actor and plan['author']['execution_id']==execution,'Wrong plan author')
        need(sorted(plan['publication_paths'])==paths and plan_path in paths,'Preparation not exact plan files')
        for path in paths:
            need(git(root,'cat-file','-e',old+':'+path,optional=True) is None,'Preparation rewrites existing path')
            need(path.startswith(('docs/workflow/','docs/requirements/')) and path.endswith(('.md','.json')),'Plan preparation touches implementation')
        for task_id in plan['tasks']:
            task=descriptor(plan,task_id);need(task['card'] in paths and 'Publication status: planned' in git(root,'show',tree+':'+task['card']),'Missing planned card')
    else:
        state=object_json(root,tree,STATE);activation_base(root,state,prior,old)
        need(ancestor(root,state['plan_commit'],old),'Activation plan not published')
        task=descriptor(object_json(root,state['plan_commit'],state['plan_path']),state['active_task'])
        need(any(x['actor_id']==actor and x['execution_id']==execution for x in prior['activation']['authorized_executions']+state['activation']['authorized_executions']),'Unauthorized activation preparation initiator')
        publication_owner(root,actor,execution,task['task_branch'])
        need(STATE in paths and all(path in {STATE,MANIFEST,'docs/agents/readiness/environment-profile.json'} or (path=='global.json' and task['phase']=='development-foundation') or path.startswith('docs/agents/readiness/evidence/') for path in paths),'Activation preparation touches implementation')
        load_task(root,allow_unpublished_activation=True);context(root,task['purpose'],allow_unpublished_activation=True)
    audit_candidate(root,tree);public_tree(root)
    print('CANDIDATE_PREPARATION_VERIFIED '+operation+' '+tree+'; final candidate review and publication still required')
    return {'base_commit':old,'candidate_tree':tree,'diff_paths':paths}

def validate_plan(root,old,candidate,plan_path,review_path,actor,execution,live=True):
    trusted_controller(root,old);remote(root,live,old)
    need(plan_path.startswith('docs/workflow/') and plan_path.endswith('-plan-publication.json'),'Invalid plan publication filename')
    need(git(root,'rev-list','--parents','-n','1',candidate).split()==[candidate,old],'Plan publication must be one direct child commit')
    audit_candidate(root,candidate)
    old_state=object_json(root,old,STATE)
    need(any(x['actor_id']==actor and x['execution_id']==execution for x in old_state['activation']['authorized_executions']),'Unauthorized planner')
    expected=binding(root,old,candidate);plan=object_json(root,candidate,plan_path)
    need(plan['expected_old']==old and plan_path in plan['publication_paths'],'Plan old-SHA or self-path mismatch')
    need(not plan.get('bootstrap'),'One-time bootstrap cannot be reused')
    need(plan['author']['actor_id']==actor and plan['author']['execution_id']==execution,'Plan author changed')
    need(git(root,'config','pmm.actorId')==actor and git(root,'config','pmm.executionId')==execution,'Planner worktree ownership not established')
    need(plan['publication_branch'].startswith('refs/heads/dot/task/') and plan['publication_branch']!=STAGE,'Forbidden plan branch')
    publication_owner(root,actor,execution,plan['publication_branch'])
    need(git(root,'rev-parse','HEAD')==candidate and git(root,'status','--porcelain','--untracked-files=no')=='','Plan requires exact clean candidate')
    need(sorted(plan['publication_paths'])==expected['diff_paths'],'Plan publication must be exact declared files')
    for path in expected['diff_paths']:
        need(git(root,'cat-file','-e',old+':'+path,optional=True) is None,'Plan publication cannot overwrite existing file')
        need(path.startswith('docs/workflow/') or path.startswith('docs/requirements/'),'Plan publication cannot modify implementation/control')
        need(path.endswith(('.md','.json')),'Plan publication requires plain documents')
        content=git(root,'show',candidate+':'+path)
        need(not re.search(r'gh[pousr]_[A-Za-z0-9]{30,}|-----BEGIN .*PRIVATE KEY-----|libfile_[a-z0-9]{20,}',content),'Secret in plan')
    for task_id in plan['tasks']:
        task=descriptor(plan,task_id);need(task['card'] in plan['publication_paths'],'Missing new card')
        need('Publication status: planned' in git(root,'show',candidate+':'+task['card']),'Card not planned')
    checked_review(root,review_path,expected,[{'actor_id':actor,'execution_id':execution,'model':None}])
    print('PLAN_PUBLICATION_VERIFIED '+json.dumps(expected,sort_keys=True));return expected

def validate_activation(root,old,candidate,review_path,actor=None,execution=None,live=True):
    trusted_controller(root,old);remote(root,live,old);expected=binding(root,old,candidate)
    need(git(root,'rev-list','--parents','-n','1',candidate).split()==[candidate,old],'Activation must be one direct child commit')
    audit_candidate(root,candidate)
    state=object_json(root,candidate,STATE);prior=object_json(root,old,STATE)
    activation_base(root,state,prior,old)
    need(ancestor(root,state['plan_commit'],old),'Activation plan not already published')
    task=descriptor(object_json(root,state['plan_commit'],state['plan_path']),state['active_task'])
    need(any(x['actor_id']==actor and x['execution_id']==execution for x in prior['activation']['authorized_executions']+state['activation']['authorized_executions']),'Unauthorized activation initiator')
    publication_owner(root,actor,execution,task['task_branch'])
    paths=expected['diff_paths']
    need(STATE in paths and all(p in {STATE,MANIFEST,'docs/agents/readiness/environment-profile.json'} or (p=='global.json' and task['phase']=='development-foundation') or p.startswith('docs/agents/readiness/evidence/') for p in paths),'Activation contains implementation or expanded scope')
    need(state['activation']['phase']==task['phase'] and state['activation']['purpose']==task['purpose'],'Activation phase mismatch')
    # The candidate's ordinary gate checks the full immutable task and acceptance chain.
    need(git(root,'rev-parse','HEAD')==candidate and git(root,'status','--porcelain','--untracked-files=no')=='','Activation requires exact clean candidate')
    load_task(root,allow_unpublished_activation=True);context(root,task['purpose'],allow_unpublished_activation=True)
    checked_review(root,review_path,expected,state['activation']['authorized_executions']+[{'actor_id':actor,'execution_id':execution,'model':None}])
    print('ACTIVATION_VERIFIED '+json.dumps(expected,sort_keys=True));return expected

def git_target(root,actor,execution,role,action,target,candidate,expected_old,remote_name,remote_location,local_ref,live=True):
    need(target==STAGE,'Forbidden Git target');need(role=='integrator' and action in {'push','merge'},'Wrong Git role/action')
    need(remote_name=='origin' and remote_location==REPOSITORY,'Actual outgoing remote not authorized')
    state,task=load_task(root);owner(root,state,task,actor,execution)
    need(local_ref in {'HEAD',task['task_branch']},'Outgoing ref not current task')
    need(candidate==git(root,'rev-parse','HEAD'),'Outgoing candidate not HEAD')
    remote(root,live,expected_old);binding(root,expected_old,candidate);scope(root,state,task)
    print('GIT_TARGET_VERIFIED '+target+' '+expected_old+' -> '+candidate)

def delivery(root,candidate,base,evidence_path):
    state,task=load_task(root,live=True);need(base==state['activation']['base_commit'],'Delivery base is not this published activation construction base')
    evidence=read_json(v.confined(root,evidence_path))
    need(evidence['task']['path']==task['card'] and evidence['task']['author']==state['activation']['author'],'Delivery task/author mismatch')
    need(evidence['task']['scope_paths']==evidence['diff_paths'],'Delivery scope must enumerate exact diff')
    extras={evidence_path,evidence['verification_report'],evidence['review_report']}
    verification=read_json(v.confined(root,evidence['verification_report']))
    extras.update(c['output'] for c in verification['checks'])
    sidecar_blobs={path:v.blob(root,path) for path in extras}
    scope(root,state,task,extras)
    for commit in git(root,'rev-list',base+'..'+candidate).splitlines():audit_candidate(root,commit)
    context(root,task['purpose']);g.runtime(root);g.baseline(root);g.documentation(root);public_tree(root,extras)
    result=v.deliver(root,read_json(v.confined(root,MANIFEST)),MANIFEST,candidate,evidence_path,base)
    review=read_json(v.confined(root,evidence['review_report']));independent(review,state['activation']['authorized_executions'])
    if task['phase'] in DOTNET_PHASES:
        foundation_validation(root)
        need({path:v.blob(root,path) for path in extras}==sidecar_blobs,'Foundation checks changed frozen evidence')
        result=v.deliver(root,read_json(v.confined(root,MANIFEST)),MANIFEST,candidate,evidence_path,base)
    print('PMM_SUCCESSOR_DELIVERY_VERIFIED '+candidate);return result

def foundation_validation(root):
    need(any(p.endswith('/packages.lock.json') for p in git(root,'ls-files','src','tests/foundation').splitlines()),'Foundation dependency lock files missing')
    for command in [['dotnet','restore','PersonalMediaManager.sln','--locked-mode','--source','https://api.nuget.org/v3/index.json'],['dotnet','build','PersonalMediaManager.sln','--no-restore'],['dotnet','test','PersonalMediaManager.sln','--no-build','--no-restore']]:
        result=g.run(command,root);print(result.stdout);print(result.stderr,file=sys.stderr);need(result.returncode==0,'Foundation build/test failed')
        if command[1]=='test':need(re.search(r'(?:Total(?: tests)?:\s*[1-9][0-9]*|Passed:\s*[1-9][0-9]*)',result.stdout),'No executed foundation tests')
    check=v.confined(root,'tests/foundation/verify_foundation.py');result=g.run([sys.executable,str(check)],root);print(result.stdout);need(result.returncode==0 and 'FOUNDATION_INERT_AND_ISOLATED_VERIFIED' in result.stdout,'Inert/isolated foundation check missing or failed')

def publication_paths(root,state):
    """CI accepts structurally additive planned publications without skipping tests."""
    extras=set()
    current=set(git(root,'ls-files').splitlines())
    for path in sorted(current):
        if not path.startswith('docs/workflow/') or not path.endswith('-plan-publication.json') or path==state['plan_path']:continue
        publication=git(root,'log','--diff-filter=A','-1','--format=%H','HEAD','--',path)
        need(publication,'Uncommitted plan cannot alter CI scope')
        plan=object_json(root,publication,path);old=plan['expected_old']
        need(git(root,'rev-parse',publication+'^')==old,'Plan parent is not observed old stage')
        actual=binding(root,old,publication)['diff_paths']
        need(sorted(plan['publication_paths'])==actual and path in actual,'Publication includes undeclared paths')
        for name in actual:
            need(git(root,'cat-file','-e',old+':'+name,optional=True) is None,'Publication rewrites existing file')
            need(name.startswith(('docs/workflow/','docs/requirements/')) and name.endswith(('.md','.json')),'Publication changed implementation')
            need(v.blob(root,name)==git(root,'rev-parse',publication+':'+name),'Published plan was silently revised')
        for task_id in plan['tasks']:descriptor(plan,task_id)
        extras.update(actual)
    return extras

def verify(root,bootstrap=False):
    before=(git(root,'rev-parse','HEAD'),git(root,'write-tree'),{path:v.blob(root,path) for path in git(root,'ls-files').splitlines()})
    g.runtime(root);g.baseline(root);g.documentation(root);public_tree(root)
    state,task=load_task(root);scope(root,state,task,published_additions=publication_paths(root,state));context(root,task['purpose'],allow_pending=bootstrap)
    for path in ['docs/agents/readiness','tests/governance']:
        count=unittest.TestLoader().discover(str(root/path),pattern='test_*.py').countTestCases();need(count>0,'Required suite empty: '+path)
        result=subprocess.run([sys.executable,'-m','unittest','discover','-s',path,'-p','test_*.py','-v'],cwd=root)
        need(result.returncode==0,'Required verification failed: '+path)
    if task['phase'] in DOTNET_PHASES:
        changed=git(root,'diff','--name-only',state['activation']['base_commit'],'HEAD').splitlines()
        activation_only=task['phase']=='development-foundation' and changed and all(p in {STATE,MANIFEST,'docs/agents/readiness/environment-profile.json'} or p=='global.json' or p.startswith('docs/agents/readiness/evidence/') for p in changed) and not (root/'PersonalMediaManager.sln').exists()
        if activation_only:print('FOUNDATION_ACTIVATION_ONLY; SDK/context/governance checked; scaffold build/product tests NOT-RUN because implementation has not begun')
        else:foundation_validation(root)
    need(subprocess.run(['git','diff','--check'],cwd=root).returncode==0,'Whitespace check failed')
    after=(git(root,'rev-parse','HEAD'),git(root,'write-tree'),{path:v.blob(root,path) for path in git(root,'ls-files').splitlines()})
    need(before==after,'Verification changed candidate/index/tracked files')
    print('PMM_SUCCESSOR_VERIFIED; business operations remain blocked');return 0

def main(argv=None):
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('command',choices=['first-write','recover','commit','inspect','context','scope','public-tree','git-target','deliver','plan','activate','prepare-plan','prepare-activation']);p.add_argument('--root',default=str(ROOT));p.add_argument('--actor',default=os.environ.get('PMM_ACTOR'));p.add_argument('--execution',default=os.environ.get('PMM_EXECUTION'));p.add_argument('--purpose',default=os.environ.get('PMM_PURPOSE','governance'));p.add_argument('--candidate');p.add_argument('--base');p.add_argument('--evidence');p.add_argument('--review');p.add_argument('--plan');p.add_argument('--old-sha');p.add_argument('--role');p.add_argument('--action');p.add_argument('--target');p.add_argument('--remote-name');p.add_argument('--remote-location');p.add_argument('--local-ref');a=p.parse_args(argv)
    try:
        root=v.root_path(a.root)
        if a.command in {'plan','activate'} and a.target is not None:
            need(a.target==STAGE and a.remote_name=='origin' and a.remote_location==REPOSITORY,'Forbidden actual plan/activation outgoing target')
            need(a.local_ref in {'HEAD',git(root,'symbolic-ref','--quiet','HEAD',optional=True)},'Outgoing publication ref not current branch')
        if a.command in {'first-write','recover','commit'}:preflight(root,a.command,a.actor,a.execution,a.purpose)
        elif a.command in {'prepare-plan','prepare-activation'}:prepare(root,'plan' if a.command=='prepare-plan' else 'activate',a.old_sha,a.actor,a.execution,a.plan)
        elif a.command=='context':context(root,a.purpose)
        elif a.command=='inspect':print(json.dumps(v.observed_context(root,read_json(root/MANIFEST)),indent=2))
        elif a.command=='scope':state,task=load_task(root);scope(root,state,task)
        elif a.command=='public-tree':public_tree(root)
        elif a.command=='git-target':git_target(root,a.actor,a.execution,a.role,a.action,a.target,a.candidate,a.old_sha,a.remote_name,a.remote_location,a.local_ref)
        elif a.command=='deliver':delivery(root,a.candidate,a.base,a.evidence)
        elif a.command=='plan':validate_plan(root,a.old_sha,a.candidate,a.plan,a.review,a.actor,a.execution)
        elif a.command=='activate':validate_activation(root,a.old_sha,a.candidate,a.review,a.actor,a.execution)
        return 0
    except (v.Invalid,OSError,KeyError,ValueError,TypeError) as exc:
        print('PMM_SUCCESSOR_REJECTED: '+str(exc),file=sys.stderr);return 1
if __name__=='__main__':sys.exit(main())

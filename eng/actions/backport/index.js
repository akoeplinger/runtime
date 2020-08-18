async function run() {
  const util = require('util');
  const jsExec = util.promisify(require('child_process').exec);

  console.log(`Installing npm dependencies`);
  const { stdout, stderr } = await jsExec("npm install @actions/core @actions/github @actions/exec");
  console.log(`npm-install stderr:\n\n${stderr}`);
  console.log(`npm-install stdout:\n\n${stdout}`);
  console.log(`Finished installing npm dependencies`);

  const core = require('@actions/core');
  const github = require('@actions/github');
  const exec = require('@actions/exec');

  if (github.context.eventName !== 'issue_comment') throw "Error: This action only works on issue_comment events.";

  const payload = JSON.stringify(github.context.payload, undefined, 2)
  console.log(`The event payload: ${payload}`);

  try {
    const auth_token = core.getInput('auth_token');
    const octokit = github.getOctokit(auth_token)
    const run_id = process.env.GITHUB_RUN_ID;
    const repo_owner = github.context.payload.repository.owner.login;
    const repo_name = github.context.payload.repository.name;
    const pr_number = github.context.payload.issue.number;

    // extract the target branch name from the trigger phrase containing these characters: a-z, A-Z, digits, forward slash, dot, hyphen, underscore
    console.log(`Extracting target branch`);
    const regex = /\/backport to ([a-zA-Z\d\/\.\-\_]+)/;
    const target_branch = regex.exec(github.context.payload.comment.body)[1];
    if (target_branch == null) throw "Error: No backport branch found.";
    console.log(`Backport target branch: ${target_branch}`);

    // Post backport started comment to pull request
    const backport_start_body = `Started backporting to ${target_branch}: https://github.com/${repo_owner}/${repo_name}/actions/runs/${run_id}`;
    await octokit.issues.createComment({
      owner: repo_owner,
      repo: repo_name,
      issue_number: pr_number,
      body: backport_start_body
    });

    console.log("Applying backport patch");

    await exec.exec(`git checkout ${target_branch}`);
    await exec.exec(`git clean -xdff`);

    // configure git
    await exec.exec(`git config user.name "github-actions"`);
    await exec.exec(`git config user.email "github-actions@github.com"`);

    // create temporary backport branch
    const temp_branch = `backport/pr-${pr_number}-to-${target_branch}`;
    await exec.exec(`git checkout -b ${temp_branch}`);

    // skip opening PR if the branch already exists on the origin remote since that means it was opened
    // by an earlier backport and force pushing to the branch updates the existing PR
    let should_open_pull_request = true;
    try { should_open_pull_request = await exec.exec(`git ls-remote --exit-code --heads origin ${temp_branch}`) == 0; } catch { }

    // download and apply patch
    await exec.exec(`curl -sSL "${github.context.payload.pull_request.patch_url}" --output changes.patch`);

    const git_am_command = 'git am --3way --ignore-whitespace --keep-non-patch changes.patch';
    let git_am_output = `$ ${git_am_command}\n\n`;
    let git_am_failed = false;
    try {
      await exec.exec(git_am_command, {
        listeners: {
          stdout: function stdout(data) { git_am_output += data; },
          stderr: function stderr(data) { git_am_output += data; }
        }
      });
    } catch {
      git_am_failed = true;
    }

    console.log(git_am_output);

    if (git_am_failed) {
      throw "Error: git am failed, most likely due to a merge conflict.";
    }
    else {
      await exec.exec(`git push --force --set-upstream origin HEAD:${temp_branch}`);
    }
  } catch (error) {
    core.setFailed(error);
  }
}

run();

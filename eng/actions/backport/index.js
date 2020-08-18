async function run() {
  const util = require('util');
  const exec = util.promisify(require('child_process').exec);

  console.log(`Installing npm dependencies`);
  const { stdout, stderr } = await exec("npm install @actions/core @actions/github");
  console.log(`npm-install stderr:\n\n${stderr}`);
  console.log(`npm-install stdout:\n\n${stdout}`);
  console.log(`Finished installing npm dependencies`);

  const core = require('@actions/core');
  const github = require('@actions/github');

  if (github.context.eventName !== 'issue_comment') throw "This action only works on issue_comment events.";

  const payload = JSON.stringify(github.context.payload, undefined, 2)
  console.log(`The event payload: ${payload}`);

  try {
    const auth_token = core.getInput('auth_token');
    const octokit = github.getOctokit(auth_token)
    const run_id = github.run_id;
    const repo_owner = github.context.payload.repository.owner.login;
    const repo_name = github.context.payload.repository.name;
    const pr_number = github.context.payload.issue.number;

    // extract the target branch name from the trigger phrase containing these characters: a-z, A-Z, digits, forward slash, dot, hyphen, underscore
    console.log(`Extracting target branch`);
    var regex = /\/backport to ([a-zA-Z\d\/\.\-\_]+)/;
    const target_branch = regex.exec(github.context.payload.comment.body)[1];
    if (target_branch == null) throw "No backport branch found.";
    console.log(`Backport target branch: ` + target_branch);

    // Post backport started comment to pull request
    const backport_start_body = `Started backporting to ${target_branch}: https://github.com/${repo_owner}/${repo_name}/actions/runs/${run_id}`;

    await octokit.issues.createComment({
      repo_owner,
      repo_name,
      pr_number,
      backport_start_body
    });
  } catch (error) {
    core.setFailed(error.message);
    console.log(error.request);
  }
}

run();

const execSync = require('child_process').execSync;

console.log(`Installing npm dependencies`);
const npm_stdout = execSync("npm install @actions/core @actions/github");
console.log(`npm-install stdout: ${npm_stdout}`);
console.log(`Finished installing npm dependencies`);

const core = require('@actions/core');
const github = require('@actions/github');

try {
  const auth_token = core.getInput('auth_token');
  const octokit = github.getOctokit(auth_token)
  const run_id = github.run_id;
  const repo_owner = github.repository_owner;
  const repo_name = github.event.repository.name;
  const pr_number = github.event.issue.number;

  // extract the target branch name from the trigger phrase containing these characters: a-z, A-Z, digits, forward slash, dot, hyphen, underscore
  console.log(`Extracting target branch`);
  var regex = /\/backport to ([a-zA-Z\d\/\.\-\_]+)/;
  const target_branch = regex.exec(github.event.comment.body)[1];
  if (target_branch == null) throw "No backport branch found."
  console.log(`Backport target branch: ` + target_branch);

  // Post backport started comment to pull request
  const backport_start_body = `Started backporting to ${target_branch}: https://github.com/${repo_owner}/${repo_name}/actions/runs/${run_id}`;

  octokit.issues.createComment({
    repo_owner,
    repo_name,
    pr_number,
    backport_start_body
  });
} catch (error) {
  core.setFailed(error.message);
}

// Exercise the workflow's actual API validation code with controlled GitHub responses.
// No credentials, network calls, repository writes or real releases are used.
const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const crypto = require('node:crypto');
const workflow = fs.readFileSync(path.join(__dirname, '../.github/workflows/draft-release.yml'), 'utf8');
const scripts = [];
const lines = workflow.split(/\r?\n/);
for (let i = 0; i < lines.length; i++) {
  if (lines[i] !== '          script: |') continue;
  const block = [];
  while (++i < lines.length && (lines[i].startsWith('            ') || !lines[i].trim())) block.push(lines[i].slice(12));
  scripts.push(block.join('\n'));
  i--;
}
assert.equal(scripts.length, 3, 'Find all github-script blocks');
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
const runScript = (index, fixture) => new AsyncFunction('github', 'core', 'context', 'process', 'require', scripts[index])(
  fixture.github, fixture.core, {repo: {owner: 'owner', repo: 'tailwatch'}}, {env: fixture.env}, fixture.require ?? require
);
const sha = 'a'.repeat(40);
function sourceFixture() {
  const fixture = {
    run: {repository: {full_name: 'owner/tailwatch'}, head_repository: {full_name: 'owner/tailwatch'}, status: 'completed', conclusion: 'success', event: 'push', workflow_id: 10, path: '.github/workflows/build.yml', head_sha: sha},
    workflow: {path: '.github/workflows/build.yml'},
    artifacts: [{name: 'release-bundles', expired: false}],
    cargoVersion: '1.2.3', viewerVersion: '1.2.3',
    env: {SOURCE_RUN_ID: '1234', RELEASE_VERSION: '1.2.3'}, outputs: {}, refs: []
  };
  fixture.core = {setOutput: (key, value) => { fixture.outputs[key] = value; }};
  fixture.github = {
    rest: {
      actions: {
        getWorkflowRun: async ({run_id}) => { assert.equal(run_id, 1234); return {data: fixture.run}; },
        getWorkflow: async () => ({data: fixture.workflow}), listWorkflowRunArtifacts: 'list-artifacts'
      },
      repos: {getContent: async ({path, ref}) => {
        fixture.refs.push(ref);
        const text = path === 'agent/Cargo.toml' ? `version = "${fixture.cargoVersion}"` : `<Version>${fixture.viewerVersion}</Version>`;
        return {data: {type: 'file', encoding: 'base64', content: Buffer.from(text).toString('base64')}};
      }}
    },
    paginate: async () => fixture.artifacts
  };
  return fixture;
}
test('successful build resolves the exact SHA and reads both manifests at that SHA', async () => {
  const fixture = sourceFixture(); await runScript(0, fixture);
  assert.deepEqual(fixture.outputs, {sha, version: '1.2.3'});
  assert.deepEqual(fixture.refs, [sha, sha]);
});
for (const [name, mutate, error] of [
  ['another repository', f => { f.run.repository.full_name = 'other/repo'; }, /this repository/],
  ['fork source', f => { f.run.head_repository.full_name = 'fork/tailwatch'; }, /this repository/],
  ['failed build', f => { f.run.conclusion = 'failure'; }, /successfully/],
  ['unfinished build', f => { f.run.status = 'in_progress'; }, /successfully/],
  ['pull request build', f => { f.run.event = 'pull_request'; }, /cannot be released/],
  ['wrong workflow', f => { f.workflow.path = '.github/workflows/other.yml'; }, /build.yml/],
  ['wrong source path', f => { f.run.path = '.github/workflows/other.yml'; }, /build.yml/],
  ['agent version mismatch', f => { f.cargoVersion = '9.9.9'; }, /source manifests/],
  ['viewer version mismatch', f => { f.viewerVersion = '9.9.9'; }, /source manifests/],
  ['expired artifact', f => { f.artifacts[0].expired = true; }, /unexpired/],
  ['duplicate artifact', f => { f.artifacts.push({...f.artifacts[0]}); }, /exactly one/],
  ['invalid version input', f => { f.env.RELEASE_VERSION = '1.2.3; echo nope'; }, /semantic version/],
  ['invalid run ID', f => { f.env.SOURCE_RUN_ID = '1; echo nope'; }, /run ID/],
]) {
  test(`rejects ${name} without promoting outputs`, async () => {
    const fixture = sourceFixture(); mutate(fixture);
    await assert.rejects(runScript(0, fixture), error);
    assert.deepEqual(fixture.outputs, {});
  });
}

function publishFixture() {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'tailwatch-workflow-test-'));
  const archives = ['windows-x64', 'linux-x64', 'macos-arm64'].map(p => `Tailwatch-v1.2.3-${p}.zip`);
  const sums = [];
  for (const name of archives) {
    const content = Buffer.from(`controlled bytes for ${name}`);
    fs.writeFileSync(path.join(root, name), content);
    sums.push(`${crypto.createHash('sha256').update(content).digest('hex')}  ${name}`);
  }
  fs.writeFileSync(path.join(root, 'SHA256SUMS.txt'), sums.join('\n') + '\n');
  const fixture = {root, env: {RELEASE_VERSION: '1.2.3', SOURCE_SHA: sha}, creates: [], uploads: [], releases: [], tagExists: false};
  fixture.require = name => name === 'fs' ? {
    readdirSync: () => fs.readdirSync(root),
    readFileSync: (name, encoding) => fs.readFileSync(path.join(root, path.basename(name)), encoding)
  } : require(name);
  fixture.core = {notice: () => {}};
  fixture.github = {
    rest: {
      git: {getRef: async () => { if (fixture.tagExists) return {data: {}}; throw Object.assign(new Error('not found'), {status: 404}); }},
      repos: {
        listReleases: 'list-releases',
        createRelease: async args => { fixture.creates.push(args); return {data: {id: 23, html_url: 'https://example.invalid/draft'}}; },
        uploadReleaseAsset: async args => { fixture.uploads.push(args); }
      }
    },
    paginate: async () => fixture.releases
  };
  return fixture;
}
function removeFixture(root) {
  const resolved = path.resolve(root);
  assert.equal(path.dirname(resolved), path.resolve(os.tmpdir()), 'Cleanup must stay in the temporary directory');
  assert.ok(path.basename(resolved).startsWith('tailwatch-workflow-test-'), 'Cleanup must target this test fixture');
  fs.rmSync(resolved, {recursive: true});
}
test('creates only a draft prerelease at the verified SHA and attaches all four files', async () => {
  const fixture = publishFixture();
  try {
    await runScript(1, fixture); await runScript(2, fixture);
    assert.equal(fixture.creates.length, 1);
    const release = fixture.creates[0];
    assert.equal(release.target_commitish, sha); assert.equal(release.tag_name, 'v1.2.3');
    assert.equal(release.draft, true); assert.equal(release.prerelease, true);
    assert.equal(fixture.uploads.length, 4);
    for (const upload of fixture.uploads) {
      assert.equal(upload.release_id, 23);
      assert.deepEqual(upload.data, fs.readFileSync(path.join(fixture.root, upload.name)));
    }
  } finally { removeFixture(fixture.root); }
});
for (const [name, mutate, error] of [
  ['existing tag', f => { f.tagExists = true; }, /already exists/],
  ['existing published release', f => { f.releases = [{tag_name: 'v1.2.3', draft: false}]; }, /already exists/],
  ['existing draft', f => { f.releases = [{tag_name: 'v1.2.3', draft: true}]; }, /already exists/],
  ['changed archive', f => { fs.appendFileSync(path.join(f.root, 'Tailwatch-v1.2.3-windows-x64.zip'), 'tampered'); }, /Checksum mismatch/],
  ['extra asset', f => { fs.writeFileSync(path.join(f.root, 'secret.txt'), 'private'); }, /Unexpected release bundle files/],
]) {
  test(`publishing rejects ${name} before creating a release`, async () => {
    const fixture = publishFixture();
    try { mutate(fixture); await assert.rejects(runScript(2, fixture), error); assert.equal(fixture.creates.length, 0); assert.equal(fixture.uploads.length, 0); }
    finally { removeFixture(fixture.root); }
  });
}

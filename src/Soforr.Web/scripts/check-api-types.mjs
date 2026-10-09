// Verifies that src/api/schema.d.ts is exactly what `npm run api:types` generates from openapi/openapi.json,
// without modifying anything in the working tree: it generates into a temporary folder and compares.
// Run it with `npm run api:types:check` from src/Soforr.Web. Exit code 1 means the committed types are stale.
import { execFileSync } from 'node:child_process'
import { mkdtempSync, readFileSync, rmSync } from 'node:fs'
import { tmpdir } from 'node:os'
import { join } from 'node:path'
import { fileURLToPath } from 'node:url'

const root = fileURLToPath(new URL('..', import.meta.url))
const cli = join(root, 'node_modules', 'openapi-typescript', 'bin', 'cli.js')
const committedPath = join(root, 'src', 'api', 'schema.d.ts')
const folder = mkdtempSync(join(tmpdir(), 'soforr-api-types-'))
const generatedPath = join(folder, 'schema.d.ts')

// Line endings differ between checkouts (Git may convert them), the content must not.
const normalize = (text) => text.replace(/\r\n/g, '\n')

try {
  execFileSync(process.execPath, [cli, 'openapi/openapi.json', '-o', generatedPath], {
    cwd: root,
    stdio: ['ignore', 'ignore', 'inherit'],
  })

  if (
    normalize(readFileSync(committedPath, 'utf8')) !==
    normalize(readFileSync(generatedPath, 'utf8'))
  ) {
    console.error(
      'src/api/schema.d.ts is out of date with openapi/openapi.json. Run `npm run api:types` and commit the result.',
    )
    process.exitCode = 1
  } else {
    console.log('src/api/schema.d.ts matches openapi/openapi.json.')
  }
} finally {
  rmSync(folder, { recursive: true, force: true })
}

import { describe, expect, it } from 'vitest'
import { networkProblem, toProblem } from './problem'

describe('toProblem', () => {
  it('uses the detail the server wrote, and its code', () => {
    const problem = toProblem(
      { code: 'product.sku_conflict', detail: 'A product with this SKU already exists.' },
      409,
    )

    expect(problem).toEqual({
      status: 409,
      code: 'product.sku_conflict',
      message: 'A product with this SKU already exists.',
      fieldErrors: {},
    })
  })

  it('collects the messages per field and falls back to the first one as the message', () => {
    const problem = toProblem(
      {
        code: 'x',
        errors: { sku: ['The SKU is required.'], sellingPrice: ['Too many decimals.', 'Other.'] },
      },
      400,
    )

    expect(problem.fieldErrors).toEqual({
      sku: ['The SKU is required.'],
      sellingPrice: ['Too many decimals.', 'Other.'],
    })
    expect(problem.message).toBe('The SKU is required.')
  })

  it.each([
    [401, 'You need to sign in.'],
    [403, 'You are not allowed to do this.'],
    [404, 'That could not be found.'],
    [429, 'Too many attempts. Wait a moment and try again.'],
    [500, 'Something went wrong on our side. Try again.'],
    [418, 'That did not work.'],
  ])('says something sensible for a %i with no body', (status, message) => {
    expect(toProblem(undefined, status).message).toBe(message)
  })

  it('never throws on a body of the wrong shape', () => {
    expect(() => toProblem('text', 500)).not.toThrow()
    expect(() =>
      toProblem({ errors: { sku: 'not a list' }, detail: 5, code: 7 }, 400),
    ).not.toThrow()
    expect(toProblem({ errors: { sku: 'not a list' }, detail: 5, code: 7 }, 400)).toMatchObject({
      code: null,
      fieldErrors: {},
    })
  })

  it('describes a network failure', () => {
    expect(networkProblem()).toMatchObject({
      status: 0,
      message: expect.stringContaining('could not be reached'),
    })
  })
})

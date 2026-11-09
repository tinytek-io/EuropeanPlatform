import { t } from "@lingui/core/macro";
import { Trans } from "@lingui/react/macro";
import { Button } from "@repo/ui/components/Button";
import { Form } from "@repo/ui/components/Form";
import { InputOtp, InputOtpGroup, InputOtpSlot } from "@repo/ui/components/InputOtp";
import { Link } from "@repo/ui/components/Link";
import { Logo } from "@repo/ui/components/Logo";
import { TextField } from "@repo/ui/components/TextField";
import { mutationSubmitter } from "@repo/ui/forms/mutationSubmitter";
import { useState } from "react";

import { api } from "@/shared/lib/api/client";

// The emailed codes are uppercase letters; the field accepts letters and digits like the account sign-in.
const ONE_TIME_PASSWORD_PATTERN = "^[a-zA-Z0-9]+$";
const ONE_TIME_PASSWORD_LENGTH = 6;

interface StaffSignInFormProps {
  returnPath: string;
}

export function StaffSignInForm({ returnPath }: Readonly<StaffSignInFormProps>) {
  const [email, setEmail] = useState("");
  const startMutation = api.useMutation("post", "/api/back-office/staff-sign-in/start");

  if (startMutation.isSuccess) {
    return (
      <StaffSignInCodeForm
        email={email}
        staffSignInId={startMutation.data.staffSignInId}
        returnPath={returnPath}
        onStartOver={() => startMutation.reset()}
      />
    );
  }

  return (
    <Form
      onSubmit={mutationSubmitter(startMutation)}
      validationErrors={startMutation.error?.errors}
      validationBehavior="aria"
      className="flex w-full max-w-[22rem] flex-col items-center gap-4 pt-8 pb-4"
    >
      <Link href="/" className="cursor-pointer">
        <Logo variant="mark" className="size-12" alt={t`Logo`} />
      </Link>
      <h2>
        <Trans>Back Office - Sign in</Trans>
      </h2>
      <div className="text-center text-sm text-muted-foreground">
        <Trans>Enter your work email and we will send you a sign-in code</Trans>
      </div>
      <TextField
        name="email"
        type="email"
        label={t`Email`}
        autoFocus={true}
        required={true}
        value={email}
        onChange={setEmail}
        autoComplete="email"
        placeholder={t`yourname@example.com`}
        className="flex w-full flex-col"
        disabled={startMutation.isPending}
      />
      <Button type="submit" isPending={startMutation.isPending} className="mt-4 w-full text-center">
        {startMutation.isPending ? <Trans>Sending sign-in code...</Trans> : <Trans>Send sign-in code</Trans>}
      </Button>
    </Form>
  );
}

interface StaffSignInCodeFormProps {
  email: string;
  staffSignInId: string;
  returnPath: string;
  onStartOver: () => void;
}

function StaffSignInCodeForm({ email, staffSignInId, returnPath, onStartOver }: Readonly<StaffSignInCodeFormProps>) {
  const [oneTimePassword, setOneTimePassword] = useState("");
  const completeMutation = api.useMutation("post", "/api/back-office/staff-sign-in/{id}/complete", {
    // A full page load, so the back-office renders the signed-in shell with the new session.
    onSuccess: () => {
      globalThis.location.href = returnPath;
    }
  });

  return (
    <Form
      onSubmit={(event) => {
        event.preventDefault();
        completeMutation.mutate({ params: { path: { id: staffSignInId } }, body: { oneTimePassword } });
      }}
      validationErrors={completeMutation.error?.errors}
      validationBehavior="aria"
      className="flex w-full max-w-[22rem] flex-col items-center gap-4 pt-8 pb-4"
    >
      <Logo variant="mark" className="size-12" alt={t`Logo`} />
      <h2 className="text-center">
        <Trans>Enter your sign-in code</Trans>
      </h2>
      <div className="text-center text-sm text-muted-foreground">
        <Trans>
          If <span className="font-semibold">{email}</span> belongs to a staff member, a sign-in code is on its way. It
          is valid for 5 minutes.
        </Trans>
      </div>
      <InputOtp
        containerClassName="justify-center"
        maxLength={ONE_TIME_PASSWORD_LENGTH}
        value={oneTimePassword}
        onChange={(value) => setOneTimePassword(value.toUpperCase())}
        autoFocus={true}
        inputMode="text"
        pattern={ONE_TIME_PASSWORD_PATTERN}
        aria-label={t`Sign-in code`}
        autoComplete="one-time-code"
        disabled={completeMutation.isPending}
      >
        <InputOtpGroup>
          {Array.from({ length: ONE_TIME_PASSWORD_LENGTH }, (_, index) => (
            <InputOtpSlot key={index} index={index} className="size-14" />
          ))}
        </InputOtpGroup>
      </InputOtp>
      <Button
        type="submit"
        isPending={completeMutation.isPending}
        disabled={oneTimePassword.length !== ONE_TIME_PASSWORD_LENGTH}
        className="mt-4 w-full text-center"
      >
        {completeMutation.isPending ? <Trans>Signing in...</Trans> : <Trans>Sign in</Trans>}
      </Button>
      <Button type="button" variant="link" onClick={onStartOver} disabled={completeMutation.isPending}>
        <Trans>Use a different email or request a new code</Trans>
      </Button>
    </Form>
  );
}

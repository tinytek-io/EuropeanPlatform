import { t } from "@lingui/core/macro";
import { Trans } from "@lingui/react/macro";
import { Button } from "@repo/ui/components/Button";
import {
  DialogBody,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogForm,
  DialogHeader,
  DialogTitle
} from "@repo/ui/components/Dialog";
import { DirtyDialog } from "@repo/ui/components/DirtyDialog";
import { useDialogSetDirty } from "@repo/ui/components/DirtyDialogContext";
import { TextField } from "@repo/ui/components/TextField";
import { mutationSubmitter } from "@repo/ui/forms/mutationSubmitter";
import { toast } from "sonner";

import { EmailDeliveryUnavailableAlert } from "@/shared/components/EmailDeliveryUnavailableAlert";
import { api } from "@/shared/lib/api/client";
import { handleEmailMutationError } from "@/shared/lib/emailDeliveryUnavailable";

interface InviteUserDialogProps {
  isOpen: boolean;
  onOpenChange: (isOpen: boolean) => void;
}

export default function InviteUserDialog({ isOpen, onOpenChange }: Readonly<InviteUserDialogProps>) {
  const handleClose = () => onOpenChange(false);

  return (
    <DirtyDialog open={isOpen} onOpenChange={onOpenChange} trackingTitle="Invite user">
      <DialogContent className="sm:w-dialog-md">
        <DialogHeader>
          <DialogTitle>
            <Trans>Invite user</Trans>
          </DialogTitle>
          <DialogDescription>
            <Trans>An email with login instructions will be sent to the user.</Trans>
          </DialogDescription>
        </DialogHeader>
        <InviteUserDialogBody onClose={handleClose} />
      </DialogContent>
    </DirtyDialog>
  );
}

function InviteUserDialogBody({ onClose }: { onClose: () => void }) {
  const setDirty = useDialogSetDirty();
  const inviteUserMutation = api.useMutation("post", "/api/account/users/invite", {
    onError: handleEmailMutationError,
    onSuccess: () => {
      toast.success(t`User invited successfully`);
      onClose();
    }
  });

  return (
    <DialogForm onSubmit={mutationSubmitter(inviteUserMutation)} validationErrors={inviteUserMutation.error?.errors}>
      <DialogBody>
        <TextField
          autoFocus={true}
          required={true}
          name="email"
          label={t`Email`}
          placeholder={t`user@email.com`}
          onChange={() => setDirty(true)}
        />
        <EmailDeliveryUnavailableAlert error={inviteUserMutation.error} />
      </DialogBody>
      <DialogFooter>
        <DialogClose render={<Button type="reset" variant="secondary" disabled={inviteUserMutation.isPending} />}>
          <Trans>Cancel</Trans>
        </DialogClose>
        <Button type="submit" isPending={inviteUserMutation.isPending}>
          {inviteUserMutation.isPending ? <Trans>Sending...</Trans> : <Trans>Send invite</Trans>}
        </Button>
      </DialogFooter>
    </DialogForm>
  );
}

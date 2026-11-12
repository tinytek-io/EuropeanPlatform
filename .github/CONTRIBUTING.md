# Contributing to EuropeanPlatform

Thank you for your interest in contributing to the EuropeanPlatform project! We appreciate your effort and contributions that will help improve the project. This document outlines the guidelines for contributing to the project.

Before you start working on a contribution, **we strongly recommend discussing your ideas with the project maintainers by [opening an issue here](https://github.com/tinytek-io/EuropeanPlatform/issues/new/choose)**. This ensures that your efforts align with the project's goals and that you won't spend time implementing something that may be rejected later. We have strong opinions and uphold an high standard of quality for the project, so please be prepared to make changes to your contribution based on feedback from the maintainers. Pay special attention to Branch name and Commit messages guidelines below.

By contributing, you agree that your contributions are licensed under the project's [MIT License](../LICENSE).

## Contributing Workflow

1. **Fork the Repository**: Navigate to the [EuropeanPlatform repository](https://github.com/tinytek-io/EuropeanPlatform) and click the "Fork" button in the upper right-hand corner. This will create a copy of the repository under your GitHub account.

2. **Clone the Fork**: Open a terminal window and navigate to the directory where you want to clone the forked repository. Run the following command, replacing `your-username` with your GitHub username: `git clone https://github.com/your-username/EuropeanPlatform.git`

3. **Add the Upstream Remote**: Navigate to the cloned repository and run the following command to add the original EuropeanPlatform repository as a remote named `upstream`: `git remote add upstream https://github.com/tinytek-io/EuropeanPlatform.git`

4. **Create a Branch**: Create a new branch for your contribution, giving it a descriptive name that reflects the changes you intend to make: `git checkout -b your-branch-name`. Branch names should be lowercase and use a dash to separate words.

5. **Make Changes**: Modify the code, ensuring that your changes follow the project's coding style and conventions. The code style must follow what is configured in .editorconfig. It's strongly recommended to use JetBrains Rider or ReSharper to do a full solution code cleanup before committing.

6. **Commit Your Changes**: Stage and commit your changes with descriptive commit messages. Commit messages should be formulated as a sentence in present tense, but do not include the trailing dot. All commits must be signed with GPG. Please follow the [GitHub guide](https://docs.github.com/en/authentication/managing-commit-signature-verification) on setting up GPG signing.

7. **Rebase with the Latest Changes from Upstream**: Before pushing your changes to your fork, pull and rebase the latest changes from the upstream repository to ensure that your changes are compatible with the current state of the project: `git pull upstream main`

8. **Push Your Changes**: Push your changes to your fork on GitHub: `git push origin your-branch-name`

9. **Submit a Pull Request**: Navigate to your fork on GitHub and click the "Compare & pull request" button to create a pull request. Fill out the pull request template with the necessary information and submit your request.

Please note that we intend to merge pull-requests as fast as possible (preferably within 24 hours), so please be prepared to make prompt changes to your contribution based on feedback from the maintainers. To keep the commit history clean we may ask you to force push previous commits to your branch.

## Questions and Support

If you have any questions or need help with the contribution process, feel free to [open an issue here](https://github.com/tinytek-io/EuropeanPlatform/issues/new/choose).

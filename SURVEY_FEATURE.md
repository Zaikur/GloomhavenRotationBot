# Survey Feature Documentation

## Overview

The survey system allows you to create dynamic polls and surveys that are sent to all guild members via DMs. Members can respond privately with their votes and optional feedback, and the bot automatically aggregates results and generates AI-powered insights.

## Features

- **AI-Generated Surveys**: Automatically generates up to 4 questions with 2-3 options each based on a topic prompt
- **Private DM Responses**: Each member receives a private DM with the survey questions
- **Button-Based Voting**: Members click buttons to select their responses (no vote changes allowed)
- **Anonymous Feedback**: Optional text feedback box for members to share thoughts (anonymous in results)
- **Automatic Results**: Results are posted to the announcement channel after 24 hours or when `/survey close` is used
- **AI Hot Takes**: Machine learning model summarizes feedback into witty insights
- **Survey Archive**: View past surveys and detailed results on the `/surveys` web page

## Commands

### `/survey create <topic> [description]`
Creates a new survey from a natural language prompt.

**Parameters:**
- `topic` (required): The survey topic or prompt (e.g., "Do you prefer morning or evening sessions?")
- `description` (optional): Additional context for the survey

**Workflow:**
1. Bot generates 4 questions max with 2-3 options each via AI
2. Shows preview with generated questions and confirm/cancel buttons
3. Upon confirmation, creates survey in database
4. Sends DMs to all non-bot guild members with survey questions
5. Survey auto-closes in 24 hours

### `/survey close <surveyId>`
Manually close an open survey and post results immediately.

**Parameters:**
- `surveyId` (required): The survey ID (full or first 8 characters)

### `/survey list [includeClosed]`
List recent surveys with response counts.

**Parameters:**
- `includeClosed` (optional, default: false): Include closed surveys in the list

## Web Pages

### `/surveys`
View all surveys with a summary of:
- Survey title and status
- Created date
- Response count and percentage
- Link to detailed view

### `/survey-details/{id}`
Detailed survey view showing:
- Survey questions with response breakdowns
- Per-option counts and percentages
- Response rate visualization
- AI-generated insights/hot takes
- Anonymous feedback (if provided)
- List of responding vs non-responding members

## Data Model

### Survey Table
Stores survey metadata:
- `Id`: Unique identifier (GUID)
- `Title`: Survey topic
- `Description`: Optional context
- `CreatedByUserId`: Who created the survey
- `CreatedUtc`: When it was created
- `CloseAtUtc`: Auto-close timestamp (24h from creation)
- `Status`: Open, Closed, Results Posted
- `PostChannelId`: Announcement channel for results
- `ResultsMessageId`: Discord message ID of posted results
- `HotTakes`: AI-generated summary of feedback
- `InvitedCount`: Number of members invited
- `RespondedCount`: Number of responders

### SurveyQuestion & SurveyOption Tables
- Questions belong to a survey
- Options belong to a question
- Options track response counts for fast aggregation

### SurveyResponse Table
- One row per (user, survey, question) combination
- Stores selected option ID
- Enforced unique constraint to prevent duplicate responses

### SurveyFeedback Table
- Anonymous feedback per user/survey
- Used by AI to generate hot takes
- User identity is stored internally but not displayed in results

## Implementation Details

### Services

#### SurveyService
Handles AI interaction and parsing:
- `GenerateQuestionsAsync()`: Calls AI to create questions from topic
- `GenerateHotTakesAsync()`: Creates summary from feedback

#### SurveyDmService
Manages DM delivery and response capture:
- `SendSurveyDmsAsync()`: Sends survey to all target members
- `RecordResponseAsync()`: Stores vote selection
- `RecordFeedbackAsync()`: Stores feedback text

#### SurveyAutoCloseService
Background job that:
- Runs every 5 minutes
- Checks for surveys past close time
- Posts results and generates hot takes
- Updates survey status

### MessageHandler Extensions
Processes button interactions:
- Survey option selection buttons
- Feedback modal trigger
- Survey confirm/cancel during creation

### Discord Interactions
- **Buttons**: `survey_opt_{surveyId}_{questionId}_{optionId}_{userId}` for voting
- **Buttons**: `survey_feedback_{surveyId}_{userId}` to open feedback modal
- **Modals**: `survey_feedback_submit_{surveyId}_{userId}` for feedback text input

## Configuration

The feature uses existing bot configuration:
- **Announcement Channel**: Results are posted to the channel configured in Setup UI
- **Guild**: Surveys are sent to members of the configured guild
- **AI Config**: Uses the AI provider, model, and API key from Settings

## Constraints & Behavior

- **Max 4 questions**: AI will generate at most 4 questions
- **Max 3 options per question**: Each question has 2-3 options (mutually exclusive)
- **24-hour auto-close**: Surveys close and post results after 24 hours
- **No vote changes**: Members cannot modify their response after clicking the button
- **Anonymous feedback**: Feedback text is associated with user ID in database but never exposed in results
- **All-member audience**: Every non-bot guild member receives a DM
- **DM fallback**: If DM fails, member is logged but survey continues
- **Responder tracking**: Can see who responded vs who didn't on the details page

## Workflow Example

1. **Create**: `/survey create "Should we host sessions on weekends?"`
2. **Preview**: Bot shows "Q1: Are you interested in weekend sessions? A: Yes B: No"
3. **Confirm**: User clicks "Confirm & Send"
4. **DMs sent**: Bot sends DM to all members with buttons for Yes/No
5. **Feedback**: Member optionally clicks "Add Feedback" button and types thoughts
6. **Auto-close**: After 24h or manual `/survey close`, results post to announcement channel
7. **Results**: Shows vote counts, percentages, and AI insights from feedback
8. **Archive**: Available on `/surveys` page for historical reference

## AI Prompts

### Question Generation
Instructed to generate valid JSON with constraints:
- Unambiguous, clear questions
- Mutually exclusive options
- Max 4 questions, max 3 options each
- Conversational, natural language

### Hot Takes Generation
Instructed to:
- Summarize feedback into 2-3 punchy insights
- Extract actual themes without assumptions
- Never mention user identities
- Keep witty and conversational tone
- Max 150 words

## Error Handling

- **DM failures**: Logged but don't fail survey creation; member can still see results on web
- **AI failures**: Returns default fallback survey (yes/no question) with error tag
- **DB errors**: Logged; response not recorded
- **Missing channels**: Warning logged; results not posted to channel (but survey is marked closed)

## Future Enhancements

Potential improvements:
- Allow survey creators to edit questions before sending
- Support for ranked-choice/slider options
- Custom close times (not just 24h)
- Ability to resend DMs to non-responders
- Export survey data as CSV
- Scheduled recurring surveys
- Role-based audience selection (not all members)
